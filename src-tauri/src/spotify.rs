use anyhow::{anyhow, Context, Result};
use base64::{engine::general_purpose::URL_SAFE_NO_PAD, Engine as _};
use reqwest::{header::RETRY_AFTER, Client, StatusCode, Url};
use serde::Serialize;
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::collections::HashSet;
use std::time::{Duration, Instant};
use tauri::AppHandle;
use tauri_plugin_opener::OpenerExt;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;
use tokio::sync::Mutex;

const CLIENT_ID: &str = "cd62a88a341d400eb3310c7cd4d18be6";
const REDIRECT_URI: &str = "http://127.0.0.1:43821/callback";
const SCOPES: &str = "user-read-private playlist-read-private playlist-read-collaborative user-library-read user-top-read user-read-recently-played";
const KEYRING_SERVICE: &str = "com.recodio.app";
const KEYRING_USER: &str = "spotify-refresh-token";

#[derive(Debug, Clone)]
struct TokenState {
    access_token: String,
    refresh_token: String,
    expires_at: Instant,
}

#[derive(Debug, serde::Deserialize)]
struct TokenResponse {
    access_token: String,
    expires_in: u64,
    refresh_token: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SpotifyProfile {
    pub id: String,
    pub display_name: String,
    pub image_url: Option<String>,
    pub external_url: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SpotifyPlaylist {
    pub id: String,
    pub name: String,
    pub image_url: Option<String>,
    pub external_url: String,
    pub item_count: usize,
}

#[derive(Debug, Clone)]
pub struct SpotifyTrack {
    pub id: String,
    pub name: String,
    pub artists: String,
    pub duration: Option<f64>,
    pub image_url: Option<String>,
    pub external_url: String,
}

pub struct SpotifyAuth {
    token: Mutex<Option<TokenState>>,
    login_lock: Mutex<()>,
    client: Client,
}

impl SpotifyAuth {
    pub fn new() -> Result<Self> {
        let client = Client::builder()
            .user_agent("Recodio/0.5")
            .timeout(Duration::from_secs(25))
            .build()?;
        Ok(Self {
            token: Mutex::new(None),
            login_lock: Mutex::new(()),
            client,
        })
    }

    pub async fn has_session(&self) -> bool {
        if self.token.lock().await.is_some() {
            return true;
        }
        stored_refresh_token().is_some()
    }

    pub async fn login(&self, app: &AppHandle) -> Result<SpotifyProfile> {
        let _login = self
            .login_lock
            .try_lock()
            .map_err(|_| anyhow!("Ya hay un inicio de sesión de Spotify en curso"))?;

        // Se enlaza antes de abrir el navegador: así el callback nunca llega
        // durante una ventana en la que Recodio todavía no esté escuchando.
        let listener = TcpListener::bind("127.0.0.1:43821")
            .await
            .context("El puerto 43821 está ocupado; cierra la otra instancia de Recodio")?;
        let verifier = format!(
            "{}{}",
            uuid::Uuid::new_v4().simple(),
            uuid::Uuid::new_v4().simple()
        );
        let challenge = URL_SAFE_NO_PAD.encode(Sha256::digest(verifier.as_bytes()));
        let expected_state = uuid::Uuid::new_v4().simple().to_string();

        let mut auth_url = Url::parse("https://accounts.spotify.com/authorize")?;
        auth_url.query_pairs_mut().extend_pairs([
            ("client_id", CLIENT_ID),
            ("response_type", "code"),
            ("redirect_uri", REDIRECT_URI),
            ("scope", SCOPES),
            ("code_challenge_method", "S256"),
            ("code_challenge", &challenge),
            ("state", &expected_state),
        ]);
        app.opener()
            .open_url(auth_url.as_str(), None::<&str>)
            .context("No se pudo abrir Spotify en el navegador")?;

        let callback = tokio::time::timeout(Duration::from_secs(180), listener.accept())
            .await
            .map_err(|_| anyhow!("Spotify no respondió a tiempo; vuelve a iniciar sesión"))??;
        let (mut stream, _) = callback;
        let mut request = vec![0_u8; 16 * 1024];
        let read = tokio::time::timeout(Duration::from_secs(10), stream.read(&mut request))
            .await
            .map_err(|_| anyhow!("El callback de Spotify tardó demasiado"))??;
        request.truncate(read);

        let parsed = parse_callback(&request, &expected_state);
        let ok = parsed.is_ok();
        let body = if ok {
            "<!doctype html><meta charset=utf-8><title>Recodio</title><style>body{font:16px system-ui;background:#0b0d14;color:#eef2ff;display:grid;place-items:center;height:100vh;margin:0}main{max-width:32rem;text-align:center}b{color:#1ed760}</style><main><h1>Spotify conectado</h1><p>Ya puedes cerrar esta pestaña y volver a <b>Recodio</b>.</p></main>"
        } else {
            "<!doctype html><meta charset=utf-8><title>Recodio</title><style>body{font:16px system-ui;background:#0b0d14;color:#eef2ff;display:grid;place-items:center;height:100vh;margin:0}main{max-width:32rem;text-align:center}b{color:#ff6b7a}</style><main><h1>No se pudo conectar</h1><p>Vuelve a <b>Recodio</b> e inténtalo otra vez.</p></main>"
        };
        let response = format!(
            "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
            body.len(),
            body
        );
        let _ = stream.write_all(response.as_bytes()).await;
        let code = parsed?;

        let token_response = self
            .client
            .post("https://accounts.spotify.com/api/token")
            .form(&[
                ("client_id", CLIENT_ID),
                ("grant_type", "authorization_code"),
                ("code", &code),
                ("redirect_uri", REDIRECT_URI),
                ("code_verifier", &verifier),
            ])
            .send()
            .await?;
        let tokens = parse_token_response(token_response).await?;
        let refresh = tokens
            .refresh_token
            .ok_or_else(|| anyhow!("Spotify no devolvió un token para mantener la sesión"))?;
        store_refresh_token(&refresh)?;
        *self.token.lock().await = Some(TokenState {
            access_token: tokens.access_token,
            refresh_token: refresh,
            expires_at: expiry(tokens.expires_in),
        });

        self.profile().await
    }

    pub async fn logout(&self) {
        *self.token.lock().await = None;
        clear_refresh_token();
    }

    pub async fn profile(&self) -> Result<SpotifyProfile> {
        let value = self.api_json("/me").await?;
        let id = string_at(&value, "/account_id")
            .or_else(|| string_at(&value, "/id"))
            .unwrap_or_else(|| "spotify".into());
        Ok(SpotifyProfile {
            id,
            display_name: string_at(&value, "/display_name")
                .unwrap_or_else(|| "Cuenta de Spotify".into()),
            image_url: string_at(&value, "/images/0/url"),
            external_url: string_at(&value, "/external_urls/spotify"),
        })
    }

    pub async fn playlists(&self) -> Result<Vec<SpotifyPlaylist>> {
        let mut result = Vec::new();
        let mut offset = 0_usize;
        loop {
            let page = self
                .api_json(&format!("/me/playlists?limit=50&offset={offset}"))
                .await?;
            let items = page
                .get("items")
                .and_then(Value::as_array)
                .ok_or_else(|| anyhow!("Spotify devolvió una lista de playlists inesperada"))?;
            if items.is_empty() {
                break;
            }
            for item in items {
                let Some(id) = string_at(item, "/id") else {
                    continue;
                };
                result.push(SpotifyPlaylist {
                    external_url: string_at(item, "/external_urls/spotify")
                        .unwrap_or_else(|| format!("https://open.spotify.com/playlist/{id}")),
                    id,
                    name: string_at(item, "/name").unwrap_or_else(|| "Playlist".into()),
                    image_url: string_at(item, "/images/0/url"),
                    item_count: item
                        .pointer("/items/total")
                        .or_else(|| item.pointer("/tracks/total"))
                        .and_then(Value::as_u64)
                        .unwrap_or(0) as usize,
                });
            }
            offset += items.len();
            if offset >= page.get("total").and_then(Value::as_u64).unwrap_or(0) as usize {
                break;
            }
        }
        Ok(result)
    }

    pub async fn saved_tracks(&self) -> Result<Vec<SpotifyTrack>> {
        self.paged_tracks("/me/tracks", TrackShape::Wrapped).await
    }

    pub async fn top_tracks(&self) -> Result<Vec<SpotifyTrack>> {
        let value = self
            .api_json("/me/top/tracks?limit=50&time_range=medium_term")
            .await?;
        Ok(tracks_from_page(&value, TrackShape::Direct))
    }

    pub async fn recent_tracks(&self) -> Result<Vec<SpotifyTrack>> {
        let value = self.api_json("/me/player/recently-played?limit=50").await?;
        Ok(tracks_from_page(&value, TrackShape::Wrapped))
    }

    pub async fn playlist_tracks(&self, id: &str) -> Result<Vec<SpotifyTrack>> {
        if id.is_empty() || id.len() > 100 || !id.chars().all(|c| c.is_ascii_alphanumeric()) {
            return Err(anyhow!(
                "La playlist de Spotify no tiene un identificador válido"
            ));
        }
        self.paged_tracks(&format!("/playlists/{id}/items"), TrackShape::PlaylistItem)
            .await
    }

    async fn paged_tracks(&self, path: &str, shape: TrackShape) -> Result<Vec<SpotifyTrack>> {
        let mut result = Vec::new();
        let mut seen = HashSet::new();
        let mut offset = 0_usize;
        loop {
            let separator = if path.contains('?') { '&' } else { '?' };
            let page = self
                .api_json(&format!("{path}{separator}limit=50&offset={offset}"))
                .await?;
            let items = page
                .get("items")
                .and_then(Value::as_array)
                .ok_or_else(|| anyhow!("Spotify devolvió una colección inesperada"))?;
            if items.is_empty() {
                break;
            }
            for track in tracks_from_page(&page, shape) {
                if seen.insert(track.id.clone()) {
                    result.push(track);
                }
            }
            offset += items.len();
            if offset >= page.get("total").and_then(Value::as_u64).unwrap_or(0) as usize {
                break;
            }
        }
        Ok(result)
    }

    async fn api_json(&self, path: &str) -> Result<Value> {
        for attempt in 0..=2 {
            let token = self.access_token().await?;
            let response = self
                .client
                .get(format!("https://api.spotify.com/v1{path}"))
                .bearer_auth(token)
                .send()
                .await?;

            if response.status() == StatusCode::UNAUTHORIZED && attempt == 0 {
                if let Some(token) = self.token.lock().await.as_mut() {
                    token.expires_at = Instant::now();
                }
                continue;
            }
            if response.status() == StatusCode::TOO_MANY_REQUESTS && attempt < 2 {
                let wait = response
                    .headers()
                    .get(RETRY_AFTER)
                    .and_then(|h| h.to_str().ok())
                    .and_then(|s| s.parse::<u64>().ok())
                    .unwrap_or(2)
                    .min(10);
                let body = response.text().await.unwrap_or_default();
                if body.contains("QUOTA_EXCEEDED") {
                    return Err(anyhow!(
                        "Se agotó temporalmente la cuota de Spotify para aplicaciones en desarrollo"
                    ));
                }
                tokio::time::sleep(Duration::from_secs(wait)).await;
                continue;
            }

            let status = response.status();
            let body = response.text().await?;
            if !status.is_success() {
                return Err(anyhow!(spotify_api_error(status, &body)));
            }
            return serde_json::from_str(&body).context("Spotify devolvió una respuesta inválida");
        }
        Err(anyhow!("Spotify limitó demasiadas solicitudes seguidas"))
    }

    async fn access_token(&self) -> Result<String> {
        let mut guard = self.token.lock().await;
        if let Some(token) = guard.as_ref() {
            if token.expires_at > Instant::now() + Duration::from_secs(30) {
                return Ok(token.access_token.clone());
            }
        }

        let refresh = guard
            .as_ref()
            .map(|t| t.refresh_token.clone())
            .or_else(stored_refresh_token)
            .ok_or_else(|| anyhow!("Inicia sesión con Spotify para continuar"))?;
        let response = self
            .client
            .post("https://accounts.spotify.com/api/token")
            .form(&[
                ("client_id", CLIENT_ID),
                ("grant_type", "refresh_token"),
                ("refresh_token", refresh.as_str()),
            ])
            .send()
            .await?;
        let tokens = match parse_token_response(response).await {
            Ok(tokens) => tokens,
            Err(error) => {
                *guard = None;
                clear_refresh_token();
                return Err(error.context("La sesión de Spotify caducó; inicia sesión de nuevo"));
            }
        };
        let refresh = tokens.refresh_token.unwrap_or(refresh);
        store_refresh_token(&refresh)?;
        let access = tokens.access_token.clone();
        *guard = Some(TokenState {
            access_token: tokens.access_token,
            refresh_token: refresh,
            expires_at: expiry(tokens.expires_in),
        });
        Ok(access)
    }
}

#[derive(Clone, Copy)]
enum TrackShape {
    Direct,
    Wrapped,
    PlaylistItem,
}

fn tracks_from_page(page: &Value, shape: TrackShape) -> Vec<SpotifyTrack> {
    page.get("items")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(|item| {
            let track = match shape {
                TrackShape::Direct => item,
                TrackShape::Wrapped => item.get("track").or_else(|| item.get("item"))?,
                TrackShape::PlaylistItem => item.get("item").or_else(|| item.get("track"))?,
            };
            spotify_track(track)
        })
        .collect()
}

fn spotify_track(value: &Value) -> Option<SpotifyTrack> {
    let id = string_at(value, "/id")?;
    let artists = value
        .get("artists")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(|artist| string_at(artist, "/name"))
        .collect::<Vec<_>>()
        .join(", ");
    Some(SpotifyTrack {
        external_url: string_at(value, "/external_urls/spotify")
            .unwrap_or_else(|| format!("https://open.spotify.com/track/{id}")),
        id,
        name: string_at(value, "/name").unwrap_or_else(|| "Pista".into()),
        artists,
        duration: value
            .get("duration_ms")
            .and_then(Value::as_f64)
            .map(|ms| ms / 1000.0),
        image_url: string_at(value, "/album/images/0/url"),
    })
}

fn parse_callback(request: &[u8], expected_state: &str) -> Result<String> {
    let request = std::str::from_utf8(request).context("Callback de Spotify inválido")?;
    let target = request
        .lines()
        .next()
        .and_then(|line| line.split_whitespace().nth(1))
        .ok_or_else(|| anyhow!("Callback de Spotify incompleto"))?;
    let url = Url::parse(&format!("http://127.0.0.1:43821{target}"))?;
    if url.path() != "/callback" {
        return Err(anyhow!("Spotify devolvió una ruta de callback inesperada"));
    }
    let params: std::collections::HashMap<_, _> = url.query_pairs().into_owned().collect();
    if params.get("state").map(String::as_str) != Some(expected_state) {
        return Err(anyhow!(
            "La comprobación de seguridad de Spotify no coincide"
        ));
    }
    if let Some(error) = params.get("error") {
        return Err(anyhow!(if error == "access_denied" {
            "Cancelaste el acceso desde Spotify".to_string()
        } else {
            format!("Spotify rechazó el acceso: {error}")
        }));
    }
    params
        .get("code")
        .cloned()
        .ok_or_else(|| anyhow!("Spotify no devolvió el código de autorización"))
}

async fn parse_token_response(response: reqwest::Response) -> Result<TokenResponse> {
    let status = response.status();
    let body = response.text().await?;
    if !status.is_success() {
        return Err(anyhow!(spotify_api_error(status, &body)));
    }
    serde_json::from_str(&body).context("Spotify devolvió tokens inválidos")
}

fn spotify_api_error(status: StatusCode, body: &str) -> String {
    let json: Value = serde_json::from_str(body).unwrap_or(Value::Null);
    let message = json
        .pointer("/error/message")
        .and_then(Value::as_str)
        .or_else(|| json.get("error_description").and_then(Value::as_str))
        .or_else(|| json.get("error").and_then(Value::as_str));
    match (status, message) {
        (StatusCode::FORBIDDEN, _) => "Spotify denegó este permiso. Comprueba que tu usuario está añadido en Users Management y que el propietario tiene Premium".into(),
        (StatusCode::UNAUTHORIZED, _) => "La sesión de Spotify ya no es válida".into(),
        (_, Some(message)) => format!("Spotify: {message}"),
        _ => format!("Spotify respondió {status}"),
    }
}

fn expiry(seconds: u64) -> Instant {
    Instant::now() + Duration::from_secs(seconds.saturating_sub(10))
}

fn string_at(value: &Value, pointer: &str) -> Option<String> {
    value
        .pointer(pointer)
        .and_then(Value::as_str)
        .map(str::to_owned)
}

fn credential() -> Result<keyring::Entry> {
    keyring::Entry::new(KEYRING_SERVICE, KEYRING_USER)
        .context("No se pudo abrir el almacén seguro de credenciales")
}

fn stored_refresh_token() -> Option<String> {
    credential()
        .ok()?
        .get_password()
        .ok()
        .filter(|s| !s.is_empty())
}

fn store_refresh_token(token: &str) -> Result<()> {
    credential()?
        .set_password(token)
        .context("No se pudo guardar la sesión en el almacén seguro del sistema")
}

fn clear_refresh_token() {
    if let Ok(entry) = credential() {
        let _ = entry.delete_credential();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn callback_valida_estado_y_codigo() {
        let request = b"GET /callback?code=abc&state=seguro HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n";
        assert_eq!(parse_callback(request, "seguro").unwrap(), "abc");
        assert!(parse_callback(request, "otro").is_err());
    }

    #[test]
    fn lee_las_dos_formas_de_items_de_playlist() {
        let modern = serde_json::json!({"items": [{"item": {"id":"a","name":"A","artists":[{"name":"Artista"}]}}]});
        let legacy = serde_json::json!({"items": [{"track": {"id":"b","name":"B","artists":[{"name":"Artista"}]}}]});
        assert_eq!(
            tracks_from_page(&modern, TrackShape::PlaylistItem)[0].id,
            "a"
        );
        assert_eq!(
            tracks_from_page(&legacy, TrackShape::PlaylistItem)[0].id,
            "b"
        );
    }
}

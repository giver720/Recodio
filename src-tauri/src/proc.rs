//! Process helpers. On Windows every child must be spawned with
//! `CREATE_NO_WINDOW`, otherwise each download flashes a console window.

#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

pub fn command(program: impl AsRef<std::ffi::OsStr>) -> std::process::Command {
    #[allow(unused_mut)]
    let mut cmd = std::process::Command::new(program);
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(CREATE_NO_WINDOW);
    }
    force_utf8(cmd)
}

pub fn async_command(program: impl AsRef<std::ffi::OsStr>) -> tokio::process::Command {
    #[allow(unused_mut)]
    let mut cmd = tokio::process::Command::new(program);
    #[cfg(windows)]
    {
        cmd.creation_flags(CREATE_NO_WINDOW);
    }
    force_utf8(cmd)
}

/// yt-dlp y spotDL son Python, y Python escribe en la codificación regional del
/// sistema: en un Windows en español, cp1252. Un título como «El Gran Varón»
/// sale entonces con la ó en un solo byte, que no es UTF-8 válido y hacía
/// reventar la lectura de la salida. Estas variables le piden UTF-8.
trait ProcessEnv {
    fn set_env(&mut self, clave: &str, valor: &str);
}

impl ProcessEnv for std::process::Command {
    fn set_env(&mut self, clave: &str, valor: &str) {
        self.env(clave, valor);
    }
}

impl ProcessEnv for tokio::process::Command {
    fn set_env(&mut self, clave: &str, valor: &str) {
        self.env(clave, valor);
    }
}

fn force_utf8<C: ProcessEnv>(mut cmd: C) -> C {
    cmd.set_env("PYTHONUTF8", "1");
    cmd.set_env("PYTHONIOENCODING", "utf-8");
    cmd
}

/// Lector de líneas que no se rinde ante bytes que no sean UTF-8.
///
/// `BufReader::lines()` devuelve un error —«stream did not contain valid
/// UTF-8»— y aborta la descarga entera. Aquí los bytes inválidos se sustituyen
/// por el carácter de reemplazo: como mucho se ve un símbolo raro en un mensaje
/// de progreso, en vez de perder el archivo.
pub struct LossyLines<R> {
    reader: R,
    buf: Vec<u8>,
}

impl<R: tokio::io::AsyncBufRead + Unpin> LossyLines<R> {
    pub fn new(reader: R) -> Self {
        Self {
            reader,
            buf: Vec::new(),
        }
    }

    /// Es seguro usarlo dentro de `tokio::select!`: si otra rama gana la
    /// carrera, lo leído a medias se conserva en el búfer y la siguiente llamada
    /// continúa donde iba, sin perder la línea.
    pub async fn next_line(&mut self) -> std::io::Result<Option<String>> {
        use tokio::io::AsyncBufReadExt;

        let leidos = self.reader.read_until(b'\n', &mut self.buf).await?;
        if leidos == 0 && self.buf.is_empty() {
            return Ok(None);
        }
        let linea = String::from_utf8_lossy(&self.buf)
            .trim_end_matches(['\n', '\r'])
            .to_string();
        self.buf.clear();
        Ok(Some(linea))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// El caso real que lo destapó: «Willie Colón - El Gran Varón» en cp1252,
    /// donde la ó es el byte 0xF3.
    #[tokio::test]
    async fn lee_lineas_en_cp1252_sin_reventar() {
        let crudo: &[u8] = b"Willie Col\xf3n - El Gran Var\xf3n\nsegunda l\xednea\n";
        let mut lector = LossyLines::new(tokio::io::BufReader::new(crudo));

        let primera = lector.next_line().await.unwrap().unwrap();
        assert!(primera.starts_with("Willie Col"), "se leyó: {primera}");
        assert!(primera.ends_with('n'), "se leyó: {primera}");

        let segunda = lector.next_line().await.unwrap().unwrap();
        assert!(segunda.starts_with("segunda l"));

        assert!(lector.next_line().await.unwrap().is_none());
    }

    #[tokio::test]
    async fn el_utf8_correcto_se_lee_intacto() {
        let crudo: &[u8] = "Willie Colón - El Gran Varón\n".as_bytes();
        let mut lector = LossyLines::new(tokio::io::BufReader::new(crudo));
        assert_eq!(
            lector.next_line().await.unwrap().unwrap(),
            "Willie Colón - El Gran Varón"
        );
    }

    /// Una última línea sin salto final no debe perderse.
    #[tokio::test]
    async fn no_pierde_la_ultima_linea_sin_salto() {
        let crudo: &[u8] = b"sin salto final";
        let mut lector = LossyLines::new(tokio::io::BufReader::new(crudo));
        assert_eq!(lector.next_line().await.unwrap().unwrap(), "sin salto final");
        assert!(lector.next_line().await.unwrap().is_none());
    }
}

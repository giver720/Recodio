import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import { PopupPlayer } from "./components/PopupPlayer";
import { esVentanaPopup } from "./lib/popup";
import "./styles.css";

// La ventana flotante carga esta misma web con `#popup`; ahí no hay pestañas ni
// biblioteca, solo el mini reproductor.
const popup = esVentanaPopup();
if (popup) document.documentElement.dataset.theme = "dark";

ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
  <React.StrictMode>{popup ? <PopupPlayer /> : <App />}</React.StrictMode>,
);

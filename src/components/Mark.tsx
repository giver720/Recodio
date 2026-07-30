/**
 * La marca de Recodio: flecha de descarga sobre un ecualizador.
 *
 * Misma geometría que `tools/make_icon.py`, en coordenadas de 1024. Si cambia
 * una, hay que cambiar la otra: son el icono del ejecutable y el logo de la
 * interfaz, y verlos distintos delataría que son dos dibujos diferentes.
 */
export function Mark({ size = 18, className = "" }: { size?: number; className?: string }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 1024 1024"
      fill="currentColor"
      className={className}
      aria-hidden="true"
    >
      <rect x="456" y="165" width="112" height="235" rx="56" />
      <path d="M350 340 H674 L512 560 Z" />
      <rect x="288" y="600" width="108" height="256" rx="54" />
      <rect x="456" y="640" width="112" height="216" rx="56" />
      <rect x="624" y="578" width="108" height="278" rx="54" />
    </svg>
  );
}

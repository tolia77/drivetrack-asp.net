// NFR-26: Leaflet, vendored rather than fetched from a CDN.
//
// The library, its stylesheet and its marker images all live under wwwroot/lib/leaflet/ at the
// pinned 1.9.4, so the code the map runs on is served by this application and by nothing else.
// The tiles are the exception and are remote by nature: they come from OpenStreetMap, named
// below. Without a route to it the map draws its controls and its marker over empty tiles rather
// than failing to load at all.
//
// The stylesheet is injected here, at first use, rather than linked from App.razor. Two reasons:
// DesignTokenTests pins the shell to exactly two <link rel="stylesheet"> elements, and Leaflet's
// CSS refers to its images by relative url(), so it has to be served from beside them.

const stylesheetId = "dt-leaflet-stylesheet";
const stylesheetHref = "/lib/leaflet/leaflet.css";
const libraryHref = "/lib/leaflet/leaflet-src.esm.js";
const imagePath = "/lib/leaflet/images/";

// OpenStreetMap's own tiles, with the attribution their terms require. This is the one remote
// dependency the map has, and it is data rather than code.
const tileUrl = "https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png";
// "contributors" is not decoration: it is the wording OpenStreetMap's terms require.
const tileAttribution =
    '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors';
const maxZoom = 19;

let leaflet = null;

function ensureStylesheet() {
    if (document.getElementById(stylesheetId)) {
        return;
    }

    const link = document.createElement("link");
    link.id = stylesheetId;
    link.rel = "stylesheet";
    link.href = stylesheetHref;
    document.head.appendChild(link);
}

async function library() {
    if (leaflet === null) {
        ensureStylesheet();
        leaflet = await import(libraryHref);

        // Leaflet works its marker images out from the path its own script was loaded from, which
        // is wrong when the module is imported rather than linked. Stating it is the fix.
        leaflet.Icon.Default.imagePath = imagePath;
    }

    return leaflet;
}

/**
 * Creates the map, its single marker and - when picking - its click handler.
 * @param {string} containerId The id of the element the map is built into.
 * @param {object} dotNetRef A reference to the owning DtMap component.
 * @param {number} latitude Centre latitude in decimal degrees.
 * @param {number} longitude Centre longitude in decimal degrees.
 * @param {number} zoom Initial zoom level.
 * @param {boolean} picker True to let a click choose a point.
 * @returns {object} The handle setView and destroy are given back.
 */
export async function create(containerId, dotNetRef, latitude, longitude, zoom, picker) {
    const L = await library();
    const container = document.getElementById(containerId);

    if (!container) {
        return null;
    }

    const map = L.map(container).setView([latitude, longitude], zoom);

    L.tileLayer(tileUrl, { attribution: tileAttribution, maxZoom }).addTo(map);

    const marker = L.marker([latitude, longitude]).addTo(map);
    const handle = { map, marker };

    // Leaflet measures the container once, here, and never looks again. That is wrong for every
    // map this application builds inside a <dialog>: the form's two pickers are rendered with the
    // rest of the dialog's markup, so they are created while it is still display:none, measured at
    // nothing, and left painting tiles for an area the size they were told about - the grey band
    // beside a strip of map. Opening the dialog changes the element's size without telling Leaflet.
    //
    // Observing the element covers that and the two neighbours of it: the pickers sit in a
    // responsive row that reflows at `lg`, and the window itself resizes. The read-only map never
    // showed the bug - it is built behind an @if that only becomes true once its dialog is already
    // open - which is exactly why the fix belongs here rather than in whatever opens a dialog.
    if (typeof ResizeObserver === "function") {
        handle.resizeObserver = new ResizeObserver(() => {
            // A closed dialog measures zero. Re-measuring into that would cache the wrong size
            // again, so the observer waits for the element to actually have one.
            if (container.clientWidth === 0 || container.clientHeight === 0) {
                return;
            }

            map.invalidateSize();
        });

        handle.resizeObserver.observe(container);
    }

    // The picker flag is the whole guard: with it false no click handler is registered at all, so
    // a display-only map cannot move its marker or call back into .NET however hard it is clicked.
    if (picker) {
        handle.onClick = (event) => {
            // Leaflet keeps counting longitude past the antimeridian as the user pans, so a click
            // on the second copy of the world reports 190 rather than -170 - and MapLocation
            // rejects that, which would throw inside the callback and lose the pick entirely.
            // wrap() brings the longitude back into +-180; the latitude is clamped because wrap()
            // does not touch it and a click above the top edge reports past the pole.
            const point = event.latlng.wrap();
            const latitude = Math.min(90, Math.max(-90, point.lat));

            marker.setLatLng([latitude, point.lng]);
            dotNetRef.invokeMethodAsync("OnPickedAsync", latitude, point.lng);
        };

        map.on("click", handle.onClick);
    }

    return handle;
}

/**
 * Re-centres the map and moves its marker.
 * @param {object} handle The handle create returned.
 * @param {number} latitude Latitude in decimal degrees.
 * @param {number} longitude Longitude in decimal degrees.
 */
export function setView(handle, latitude, longitude) {
    if (!handle) {
        return;
    }

    handle.map.setView([latitude, longitude]);
    handle.marker.setLatLng([latitude, longitude]);
}

/**
 * Tears the map down. Leaflet registers document-level listeners, so removing the element is not
 * enough on its own.
 * @param {object} handle The handle create returned.
 */
export function destroy(handle) {
    if (!handle) {
        return;
    }

    if (handle.onClick) {
        handle.map.off("click", handle.onClick);
    }

    // Disconnected explicitly, for the reason the click handler is removed explicitly: a
    // ResizeObserver holds its target, so leaving it connected keeps a torn-down map's container
    // and closure alive for as long as the observer does.
    if (handle.resizeObserver) {
        handle.resizeObserver.disconnect();
    }

    handle.map.remove();
}

// FR-119: the signature pad's behaviour, confined to the component's own module.
//
// Pointer events rather than mouse and touch events: one set of listeners covers a finger, a stylus
// and a mouse, and setPointerCapture keeps a stroke attached to the canvas when the finger leaves
// it mid-signature - which on a phone is most strokes.
//
// State lives in a WeakMap against the element rather than in module-level variables, so two pads on
// one screen do not draw into each other's stroke, and a removed element takes its state with it.

const pads = new WeakMap();

/**
 * Prepares the canvas and wires the drawing listeners.
 * @param {HTMLCanvasElement} element The canvas element.
 */
export function attach(element) {
    if (!element || pads.has(element)) {
        return;
    }

    const context = element.getContext("2d");
    const state = { context, drawing: false, marked: false, listeners: {} };

    // The backing store is sized from the laid-out box and the device pixel ratio. Without this the
    // canvas defaults to 300x150 regardless of its CSS size, and the drawing arrives stretched and
    // offset from the finger that made it.
    resize(element, state);

    context.lineWidth = 2;
    context.lineCap = "round";
    context.lineJoin = "round";
    context.strokeStyle = readInk(element);

    const point = (event) => {
        const box = element.getBoundingClientRect();

        return { x: event.clientX - box.left, y: event.clientY - box.top };
    };

    const onDown = (event) => {
        state.drawing = true;
        state.marked = true;

        const { x, y } = point(event);

        context.beginPath();
        context.moveTo(x, y);

        // The stroke follows the pointer off the element and back, which is what makes a signature
        // drawn quickly with a finger one line rather than several.
        element.setPointerCapture(event.pointerId);

        // Stops the browser treating the drag as a scroll or a text selection. touch-action in the
        // stylesheet covers the common case; this covers the rest.
        event.preventDefault();
    };

    const onMove = (event) => {
        if (!state.drawing) {
            return;
        }

        const { x, y } = point(event);

        context.lineTo(x, y);
        context.stroke();

        event.preventDefault();
    };

    const onUp = (event) => {
        if (!state.drawing) {
            return;
        }

        state.drawing = false;

        if (element.hasPointerCapture(event.pointerId)) {
            element.releasePointerCapture(event.pointerId);
        }
    };

    state.listeners = { onDown, onMove, onUp };

    element.addEventListener("pointerdown", onDown);
    element.addEventListener("pointermove", onMove);
    element.addEventListener("pointerup", onUp);
    element.addEventListener("pointercancel", onUp);

    pads.set(element, state);
}

/**
 * The drawing as base64-encoded PNG, or null when nothing has been drawn.
 * @param {HTMLCanvasElement} element The canvas element.
 * @returns {string|null} The base64 payload, without the data URL prefix.
 */
export function read(element) {
    const state = element && pads.get(element);

    // An untouched pad answers null rather than a blank image. A transparent PNG is a file the
    // validator would accept and nobody could read, which is worse than no signature at all.
    if (!state || !state.marked) {
        return null;
    }

    const url = element.toDataURL("image/png");
    const comma = url.indexOf(",");

    return comma < 0 ? null : url.slice(comma + 1);
}

/**
 * Wipes the pad.
 * @param {HTMLCanvasElement} element The canvas element.
 */
export function clear(element) {
    const state = element && pads.get(element);

    if (!state) {
        return;
    }

    state.context.clearRect(0, 0, element.width, element.height);
    state.marked = false;
}

/**
 * Releases the listeners. Called from DisposeAsync, so a dialog opened and closed repeatedly does
 * not leave a stack of handlers behind on a detached element.
 * @param {HTMLCanvasElement} element The canvas element.
 */
export function detach(element) {
    const state = element && pads.get(element);

    if (!state) {
        return;
    }

    element.removeEventListener("pointerdown", state.listeners.onDown);
    element.removeEventListener("pointermove", state.listeners.onMove);
    element.removeEventListener("pointerup", state.listeners.onUp);
    element.removeEventListener("pointercancel", state.listeners.onUp);

    pads.delete(element);
}

/**
 * Sizes the backing store to the laid-out box, scaled for the device pixel ratio so the line is
 * crisp on a phone rather than a blurred approximation of one.
 * @param {HTMLCanvasElement} element The canvas element.
 * @param {object} state The pad's state.
 */
function resize(element, state) {
    const box = element.getBoundingClientRect();
    const ratio = window.devicePixelRatio || 1;

    // A zero-width box happens when the pad is inside a dialog that has not been shown yet. Falling
    // back to the attribute size keeps the canvas usable instead of leaving it one pixel wide.
    element.width = Math.max(1, Math.round((box.width || element.clientWidth || 300) * ratio));
    element.height = Math.max(1, Math.round((box.height || element.clientHeight || 150) * ratio));

    state.context.setTransform(ratio, 0, 0, ratio, 0, 0);
}

/**
 * The ink colour, read from the design token the stylesheet publishes rather than written here.
 * NFR-29 keeps literal palette values out of everything but the token file, and a canvas stroke is
 * as visible as a border.
 * @param {HTMLCanvasElement} element The canvas element.
 * @returns {string} A CSS colour.
 */
function readInk(element) {
    const value = getComputedStyle(element).getPropertyValue("--dt-text-primary").trim();

    // currentColor is not a value a canvas context understands, so the computed text colour is the
    // fallback when the token is not resolvable - which is what a test harness with no stylesheet
    // sees.
    return value || getComputedStyle(element).color;
}

// FR-119: the signature pad's behaviour, confined to the component's own module.
//
// Pointer events rather than mouse and touch events: one set of listeners covers a finger, a stylus
// and a mouse, and setPointerCapture keeps a stroke attached to the canvas when the finger leaves
// it mid-signature - which on a phone is most strokes.
//
// State lives in a WeakMap against the element rather than in module-level variables, so two pads on
// one screen do not draw into each other's stroke, and a removed element takes its state with it.
//
// The component's own reference is part of that state. `marked` is the flag this module already kept
// and never told .NET about, so the pad had no way to draw the design's signed state: the stroke
// happens here and the markup is rendered there. One message per signature closes that gap - the
// transition is reported, never the movement, so a name written quickly with a finger is one call up
// the circuit rather than one per pointermove.

const pads = new WeakMap();

/**
 * Prepares the canvas and wires the drawing listeners.
 * @param {HTMLCanvasElement} element The canvas element.
 * @param {object} [component] The .NET component to report the first stroke to.
 */
export function attach(element, component) {
    if (!element || pads.has(element)) {
        return;
    }

    const context = element.getContext("2d");
    const state = { context, component, drawing: false, marked: false, listeners: {} };

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

        // Reported on the transition only. Every later stroke of the same signature finds the flag
        // already set and says nothing, which is what keeps this one message rather than one per
        // press on a pad somebody is still writing on.
        if (!state.marked) {
            state.marked = true;

            report(state);
        }

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
 * Whether anything has been drawn on the pad.
 *
 * The same flag `read()` decides on, published so the component can render the design's signed state
 * from the browser's own answer instead of keeping a second copy of it in C#. A second copy is free
 * to disagree with the bytes on the canvas - a pad reading "empty" over a stroke, or the reverse -
 * and which of the two is right would then be decided by whichever was written last.
 * @param {HTMLCanvasElement} element The canvas element.
 * @returns {boolean} True once there is a stroke on the pad.
 */
export function marked(element) {
    const state = element && pads.get(element);

    return Boolean(state && state.marked);
}

/**
 * Wipes the pad.
 *
 * Nothing is reported back: this is called FROM .NET, which reads `marked` itself once the wipe has
 * happened. Calling in would be the component telling itself what it had just asked for.
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
 * Tells the component that the pad has been marked.
 *
 * Fire and forget, with the rejection swallowed. The circuit can go away between a finger touching
 * the glass and the message arriving, and an unhandled rejection on a pad somebody is still drawing
 * on would be a console error about a signature that is going perfectly well - the drawing itself
 * does not depend on the server hearing about it.
 * @param {object} state The pad's state.
 */
function report(state) {
    if (!state.component) {
        return;
    }

    state.component.invokeMethodAsync("NotifyMarkedAsync").catch(() => {
        // The circuit is gone. There is nothing left to tell, and nothing here to do about it.
    });
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
 *
 * `signature-ink` rather than the body text colour: the two carry the same value today, but a
 * signature is a mark on a pad rather than text on a surface, and the palette names it separately
 * so it can be changed without repainting every sentence in the app.
 * @param {HTMLCanvasElement} element The canvas element.
 * @returns {string} A CSS colour.
 */
function readInk(element) {
    const value = getComputedStyle(element).getPropertyValue("--dt-signature-ink").trim();

    // currentColor is not a value a canvas context understands, so the computed text colour is the
    // fallback when the token is not resolvable - which is what a test harness with no stylesheet
    // sees.
    return value || getComputedStyle(element).color;
}

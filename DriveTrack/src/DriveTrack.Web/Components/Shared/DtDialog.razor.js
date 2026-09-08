// NFR-19: the dialog's behaviour, confined to the component's own module.
//
// showModal() supplies focus trapping, Escape dismissal and focus restoration; the only thing the
// platform does not give us is backdrop dismissal, and that is the three lines below. Everything
// else here exists so that EVERY dismissal - Escape, the backdrop, the close action - arrives in
// .NET the same way: through the element's own `close` event, invoked back onto the component.
//
// Relying on Blazor's @onclose handler instead would put the same guarantee on the event-handler
// registry; a DotNetObjectReference call is deterministic and reads the same in a test.

const listeners = new WeakMap();

/**
 * Opens the dialog modally and wires its dismissal paths.
 * @param {HTMLDialogElement} element The dialog element.
 * @param {object} dotNetRef A reference to the owning DtDialog component.
 */
export function open(element, dotNetRef) {
    if (!element || element.open) {
        return;
    }

    // Re-registering on a second open would notify .NET twice for one dismissal, so the handlers
    // are attached once and remembered against the element.
    if (!listeners.has(element)) {
        const onClose = () => dotNetRef.invokeMethodAsync("NotifyClosedAsync");

        // The backdrop is not a child element: a click on it reports the dialog itself as the
        // target, while a click anywhere inside the panel reports a descendant. That identity
        // check is the whole guard - without it, clicking the panel would close the dialog.
        const onClick = (event) => {
            if (event.target === element) {
                element.close();
            }
        };

        element.addEventListener("close", onClose);
        element.addEventListener("click", onClick);
        listeners.set(element, { onClose, onClick });
    }

    element.showModal();
}

/**
 * Closes the dialog. The `close` event this raises is what notifies .NET.
 * @param {HTMLDialogElement} element The dialog element.
 */
export function close(element) {
    if (element && element.open) {
        element.close();
    }
}

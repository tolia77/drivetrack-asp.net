// FR-70, FR-75 / NFR-26: the SignalR browser client, vendored rather than fetched from a CDN.
//
// The bundle lives under wwwroot/lib/signalr/ at a pinned version of the same major as the server,
// so the code the chat screen runs on is served by this application and by nothing else. A library
// fetched from the public internet works on a developer's machine and leaves a dead screen in a
// network the customer controls.
//
// It is a UMD bundle that assigns window.signalR rather than an ES module, so it is injected as a
// classic script at first use rather than imported - the same lazy, CDN-free treatment
// DtMap.razor.js gives Leaflet's stylesheet, and for the same reason: nothing is fetched by a caller
// who never opens the screen, and App.razor keeps the two stylesheet links it is pinned to.

const libraryHref = "/lib/signalr/signalr.js";
const hubUrl = "/hubs/chat";

let loading = null;

function library() {
    if (window.signalR) {
        return Promise.resolve(window.signalR);
    }

    // Remembered, so two components mounting in the same instant inject one script rather than two.
    if (loading === null) {
        loading = new Promise((resolve, reject) => {
            const script = document.createElement("script");
            script.src = libraryHref;

            // A script that loaded but defined no global is a failure wearing a success: resolving
            // with undefined would put the error at `new signalR.HubConnectionBuilder()`, which
            // names neither the file nor the fetch.
            script.onload = () => {
                if (window.signalR) {
                    resolve(window.signalR);
                    return;
                }

                loading = null;
                reject(new Error("The SignalR client loaded but defined no global."));
            };

            // Forgotten before the rejection, so the next caller injects the script again. A
            // remembered rejected promise is handed to every later caller, and one dropped fetch
            // would leave chat dead for the life of the page.
            script.onerror = () => {
                loading = null;
                reject(new Error("The SignalR client could not be loaded."));
            };

            document.head.appendChild(script);
        });
    }

    return loading;
}

/**
 * Opens the connection and wires the one server-to-client message.
 * @param {object} dotNetRef A reference to the owning Chat component.
 * @returns {object} The handle every other function here is given.
 */
export async function connect(dotNetRef) {
    const signalR = await library();

    const connection = new signalR.HubConnectionBuilder()
        // Relative, so the connection carries the session cookie the browser already holds: the hub
        // is same-origin and outside /api, so nothing has to be minted for it.
        .withUrl(hubUrl)
        // A dropped circuit reconnects itself; this is the same courtesy for the message channel.
        .withAutomaticReconnect()
        .build();

    // The only inbound message. It carries the row the server stored - not the text that was typed -
    // so the sender's own line appears when, and only when, it is actually in the database.
    connection.on("ReceiveMessage", (driverId, message) =>
        dotNetRef.invokeMethodAsync("ReceiveMessageAsync", driverId, message));

    // The handle remembers which conversation is joined, because the server cannot: a group
    // membership is keyed on the connection id, and a reconnect is a new connection id. Without
    // rejoining, a screen that survived a transient drop looks entirely healthy - the composer
    // still sends, the server still stores - and no broadcast ever arrives again, not even the
    // caller's own lines, until the page is reloaded.
    // `closing` is set by disconnect() below, so a stop this screen asked for is not reported back
    // to it as an outage.
    const handle = { connection, joined: null, closing: false };

    // The three moments the screen has to be told about, because a dead socket looks exactly like a
    // quiet one. `withAutomaticReconnect()` retries in the background and says nothing, so a driver
    // at a doorstep goes on typing into a connection that is gone and finds out when Send fails.
    // These put that on screen instead: the dock appears, and Send is held until the hub answers
    // again.
    //
    // `onclose` as well as `onreconnecting`, and they are not the same event: the first is the
    // retry giving up for good, the second is a drop it still expects to recover from. The second
    // argument is which of the two it was, because they are two different sentences - one is a wait
    // and the other needs a way out.
    connection.onreconnecting(() =>
        dotNetRef.invokeMethodAsync("ConnectionChangedAsync", false, true));

    connection.onclose(() => {
        // Nothing is reported for a stop the component asked for. Without this, navigating away from
        // chat fires this handler during teardown: an invocation against a DotNetObjectReference
        // that is being disposed, and a StateHasChanged on a component that is going away - on every
        // single navigation, with nothing to show for it.
        if (handle.closing) {
            return;
        }

        dotNetRef.invokeMethodAsync("ConnectionChangedAsync", false, false);
    });

    connection.onreconnected(async () => {
        // Before the rejoin rather than after it: the composer comes back the moment the hub is
        // reachable, and the replay below can take as long as the conversation is.
        //
        // And its failure is not allowed to skip what follows. An interop call that rejects - a
        // circuit that has gone, a component mid-teardown - would otherwise abandon this handler
        // before the rejoin, and a SignalR group membership is keyed on the connection id: the
        // socket would be healthy, the screen would look healthy, and no broadcast would ever arrive
        // again. The dock is cosmetic here; the rejoin is not.
        try {
            await dotNetRef.invokeMethodAsync("ConnectionChangedAsync", true, true);
        } catch {
            // Nothing to tell, and nothing that changes what the socket still has to do.
        }

        if (handle.joined === null) {
            return;
        }

        // The history the rejoin answers with is replayed, not discarded. Nothing was broadcast to
        // this connection while it was down, so a rejoin that only restores the group leaves the
        // outage's messages missing from a screen that once again looks entirely healthy - and a
        // driver, who never picks a thread, has no way back to them short of reloading the page.
        // Every line goes through the same callback a broadcast does, which drops the ones already
        // on screen by id, so what lands is exactly the gap.
        const thread = await connection.invoke("JoinThread", handle.joined);

        for (const message of thread.messages) {
            await dotNetRef.invokeMethodAsync("ReceiveMessageAsync", thread.driverId, message);
        }
    });

    await connection.start();

    return handle;
}

/**
 * Joins a driver's conversation and answers with its history.
 * @param {object} handle The handle connect returned.
 * @param {number} driverId The driver row the conversation is keyed on.
 * @returns {object} The conversation.
 */
export async function joinThread(handle, driverId) {
    const thread = await handle.connection.invoke("JoinThread", driverId);

    // Remembered only once the server has accepted the join, so a refused conversation is never
    // rejoined behind the caller's back after a reconnect.
    handle.joined = driverId;

    return thread;
}

/**
 * Leaves a driver's conversation.
 * @param {object} handle The handle connect returned.
 * @param {number} driverId The driver row the conversation is keyed on.
 * @returns {Promise} Resolved once the group membership is given up.
 */
export function leaveThread(handle, driverId) {
    if (handle.joined === driverId) {
        handle.joined = null;
    }

    return handle.connection.invoke("LeaveThread", driverId);
}

/**
 * The roster of conversations, for the dispatch desk.
 * @param {object} handle The handle connect returned.
 * @returns {Array} One entry per driver.
 */
export function listThreads(handle) {
    return handle.connection.invoke("ListThreads");
}

/**
 * Sends a message. Nothing is rendered here: the server broadcasts the stored row back, and that is
 * what the screen shows.
 * @param {object} handle The handle connect returned.
 * @param {number} driverId The driver row the conversation is keyed on.
 * @param {string} text The message body.
 * @returns {Promise} Resolved once the server has stored and broadcast the row.
 */
export function send(handle, driverId, text) {
    return handle.connection.invoke("Send", driverId, text);
}

/**
 * Whether the reader is already at the newest line of a conversation.
 *
 * Asked before a new line is appended, so the answer is about where the reader was rather than
 * where the append left them. A reader who has scrolled up into last week's messages is reading;
 * pulling them to the bottom because somebody typed takes the page away mid-sentence.
 * @param {string} elementId The id of the scrolling element.
 * @returns {boolean} True when the list is at its bottom, or is too short to scroll at all.
 */
export function isAtBottom(elementId) {
    const list = document.getElementById(elementId);

    if (!list) {
        // Nothing to scroll and nothing to disturb: an unrendered list is "at the bottom" so the
        // first conversation to load is not treated as one the reader had scrolled away from.
        return true;
    }

    // A tolerance rather than an equality: a fractional device pixel, a zoomed page and a list whose
    // last line is mid-render all leave the figure a hair short, and an exact comparison reads every
    // one of them as "the reader has scrolled up".
    return list.scrollHeight - list.scrollTop - list.clientHeight <= 4;
}

/**
 * Scrolls the message list to its newest line.
 * @param {string} elementId The id of the scrolling element.
 */
export function scrollToNewest(elementId) {
    const list = document.getElementById(elementId);

    if (list) {
        list.scrollTop = list.scrollHeight;
    }
}

/**
 * Closes the connection. Called when the screen goes away, so a circuit that navigated elsewhere
 * does not leave a socket listening to a conversation nobody is reading.
 * @param {object} handle The handle connect returned.
 */
export async function disconnect(handle) {
    if (handle && handle.connection) {
        // Marked before the stop, not after: `stop()` fires `onclose` synchronously enough that a
        // flag set afterwards would already have been read as false.
        handle.closing = true;

        await handle.connection.stop();
    }
}

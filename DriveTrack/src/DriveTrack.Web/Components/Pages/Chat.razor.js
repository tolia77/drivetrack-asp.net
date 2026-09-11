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
    const handle = { connection, joined: null };

    connection.onreconnected(async () => {
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
        await handle.connection.stop();
    }
}

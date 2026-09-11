// FR-119: where the hand-over happened, read from the device rather than typed.
//
// One function, and every way it can fail answers the same thing: null. A browser with no
// geolocation API, a user who refuses the prompt, a device that cannot get a fix before the timeout
// - from .NET's point of view these are one state, "there are no coordinates", and the panel has one
// sentence for it. Distinguishing them would put a different message in front of a driver for a
// situation they resolve the same way.
//
// The promise is wrapped by hand because the platform API is callback-based and predates promises.

/**
 * The device's current position, or null when there is none to be had.
 * @returns {Promise<number[]|null>} [latitude, longitude], or null.
 */
export function currentPosition() {
    if (!navigator.geolocation) {
        return Promise.resolve(null);
    }

    return new Promise((resolve) => {
        navigator.geolocation.getCurrentPosition(
            (position) => resolve([position.coords.latitude, position.coords.longitude]),

            // Resolved rather than rejected: a refusal is an answer, not a fault, and a rejected
            // promise would surface in .NET as a JSException the panel would have to catch and
            // translate back into the same null.
            () => resolve(null),
            {
                // A doorstep fix, not a survey. High accuracy costs seconds a driver spends
                // standing still, and the point only has to say which building this was.
                enableHighAccuracy: false,
                timeout: 10000,

                // A fix from the last half-minute is the same doorstep. Re-acquiring one would be
                // ten more seconds for a coordinate that has not moved.
                maximumAge: 30000,
            });
    });
}

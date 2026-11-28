const steeringKeys = new Set(["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "w", "a", "s", "d", "W", "A", "S", "D", " "]);

// Keeps the page from scrolling on steering keys while leaving Tab and every other key alone.
export function captureSteeringKeys(board) {
    board?.addEventListener("keydown", event => {
        if (steeringKeys.has(event.key) && !event.ctrlKey && !event.metaKey && !event.altKey) event.preventDefault();
    });
}

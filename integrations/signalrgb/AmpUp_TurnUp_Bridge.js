import udp from "@SignalRGB/udp";

export function Name() { return "AmpUp Turn Up Bridge"; }
export function Version() { return "0.3.1"; }
export function VendorId() { return 0x1A86; }
export function ProductId() { return 0x55D3; }
export function Publisher() { return "AmpUp"; }
export function Type() { return "serial"; }
export function DeviceType() { return "lightingcontroller"; }
export function Size() { return getCanvasShape().size; }
export function DefaultPosition() { return [75, 70]; }
export function DefaultScale() { return 8.0; }
export function ImageUrl() { return "https://assets.signalrgb.com/devices/default/misc/usb-drive-render.png"; }

/* global
BridgePort:readonly
LightingMode:readonly
forcedColor:readonly
*/
export function ControllableParameters() {
    return [
        { property: "BridgePort", group: "bridge", label: "AmpUp Bridge Port", type: "number", min: "1024", max: "65535", step: "1", default: "45333", live: false },
        { property: "LightingMode", group: "lighting", label: "Lighting Mode", type: "combobox", values: ["Canvas", "Forced"], default: "Canvas" },
        { property: "forcedColor", group: "lighting", label: "Forced Color", type: "color", default: "#00E676" },
    ];
}

const ampUpCanvasShape = "Classic Strip";

const ledNames = [
    "Knob 1 Left", "Knob 1 Center", "Knob 1 Right",
    "Knob 2 Left", "Knob 2 Center", "Knob 2 Right",
    "Knob 3 Left", "Knob 3 Center", "Knob 3 Right",
    "Knob 4 Left", "Knob 4 Center", "Knob 4 Right",
    "Knob 5 Left", "Knob 5 Center", "Knob 5 Right",
];

const classicStripPositions = [
    [0, 0], [1, 0], [2, 0],
    [3, 0], [4, 0], [5, 0],
    [6, 0], [7, 0], [8, 0],
    [9, 0], [10, 0], [11, 0],
    [12, 0], [13, 0], [14, 0],
];

const knobGridPositions = [
    [1, 5], [2, 3], [3, 5],
    [7, 5], [8, 3], [9, 5],
    [13, 5], [14, 3], [15, 5],
    [19, 5], [20, 3], [21, 5],
    [25, 5], [26, 3], [27, 5],
];

const arcPositions = [
    [1, 6], [2, 4], [3, 6],
    [7, 4], [8, 2], [9, 4],
    [13, 3], [14, 1], [15, 3],
    [19, 4], [20, 2], [21, 4],
    [25, 6], [26, 4], [27, 6],
];

const matrixPositions = [
    [2, 1], [2, 4], [2, 7],
    [8, 1], [8, 4], [8, 7],
    [14, 1], [14, 4], [14, 7],
    [20, 1], [20, 4], [20, 7],
    [26, 1], [26, 4], [26, 7],
];

const wideStripPositions = [
    [0, 4], [2, 4], [4, 4],
    [6, 4], [8, 4], [10, 4],
    [12, 4], [14, 4], [16, 4],
    [18, 4], [20, 4], [22, 4],
    [24, 4], [26, 4], [28, 4],
];

let socket;
let lastLog = 0;

export function LedNames() {
    return ledNames;
}

export function LedPositions() {
    return getCanvasShape().positions;
}

export function Initialize() {
    socket = udp.createSocket();
    device.setName(getDeviceName());
    applyCanvasShape();
    device.setFrameRateTarget(30);
    device.log(`AmpUp bridge sending to 127.0.0.1:${getBridgePort()}`);
}

export function Render() {
    if (!socket) return;

    const positions = getCanvasShape().positions;
    const packet = [0x41, 0x55, 0x50, 0x31, positions.length];
    for (let i = 0; i < positions.length; i++) {
        const x = positions[i][0];
        const y = positions[i][1];
        const color = LightingMode === "Forced" ? hexToRgb(forcedColor) : device.color(x, y);
        packet.push(color[0], color[1], color[2]);
    }

    socket.write(packet, "127.0.0.1", getBridgePort());

    const now = Date.now();
    if (now - lastLog > 5000) {
        lastLog = now;
        device.log(`AmpUp bridge frame sent to 127.0.0.1:${getBridgePort()}`);
    }
}

export function Shutdown() {
    if (socket) {
        socket.close();
        socket = undefined;
    }
}

export function Validate(endpoint) {
    return true;
}

function hexToRgb(hex) {
    const result = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(hex || "#000000");
    if (!result) return [0, 0, 0];
    return [parseInt(result[1], 16), parseInt(result[2], 16), parseInt(result[3], 16)];
}

function getBridgePort() {
    const port = Number(BridgePort || 45333);
    return port >= 1024 && port <= 65535 ? port : 45333;
}

function getDeviceName() {
    if (typeof controller !== "undefined" && controller && controller.name) return controller.name;
    return "AmpUp Turn Up Mixer";
}

function applyCanvasShape() {
    const layout = getCanvasShape();
    device.setSize(layout.size);
    device.setControllableLeds(ledNames, layout.positions);
    device.log(`AmpUp bridge canvas shape: ${layout.name}`);
}

function getCanvasShape() {
    const shape = ampUpCanvasShape;

    if (shape === "Classic Strip") return { name: "Classic Strip", size: [15, 1], positions: classicStripPositions };
    if (shape === "Arc") return { name: "Arc", size: [29, 9], positions: arcPositions };
    if (shape === "Matrix") return { name: "Matrix", size: [29, 9], positions: matrixPositions };
    if (shape === "Wide Strip") return { name: "Wide Strip", size: [29, 9], positions: wideStripPositions };

    return { name: "Knob Grid", size: [29, 9], positions: knobGridPositions };
}

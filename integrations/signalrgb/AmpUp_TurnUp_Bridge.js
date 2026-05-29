import { udp } from "@SignalRGB/udp";

export function Name() { return "AmpUp Turn Up Bridge"; }
export function VendorId() { return 0x1A86; }
export function ProductId() { return 0x55D3; }
export function Publisher() { return "AmpUp"; }
export function Type() { return "network"; }
export function DeviceType() { return "lightingcontroller"; }
export function Size() { return [15, 1]; }
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

const ledNames = [
    "Knob 1 Left", "Knob 1 Center", "Knob 1 Right",
    "Knob 2 Left", "Knob 2 Center", "Knob 2 Right",
    "Knob 3 Left", "Knob 3 Center", "Knob 3 Right",
    "Knob 4 Left", "Knob 4 Center", "Knob 4 Right",
    "Knob 5 Left", "Knob 5 Center", "Knob 5 Right",
];

const ledPositions = [
    [0, 0], [1, 0], [2, 0],
    [3, 0], [4, 0], [5, 0],
    [6, 0], [7, 0], [8, 0],
    [9, 0], [10, 0], [11, 0],
    [12, 0], [13, 0], [14, 0],
];

let socket;
let lastLog = 0;

export function LedNames() {
    return ledNames;
}

export function LedPositions() {
    return ledPositions;
}

export function Initialize() {
    socket = udp.createSocket();
    socket.on("error", err => device.log(`AmpUp bridge UDP error: ${err}`));
    socket.connect("127.0.0.1", Number(BridgePort || 45333));
    device.setFrameRateTarget(30);
    device.log(`AmpUp bridge sending to 127.0.0.1:${BridgePort || 45333}`);
}

export function Render() {
    if (!socket) return;

    const packet = [0x41, 0x55, 0x50, 0x31, ledPositions.length];
    for (let i = 0; i < ledPositions.length; i++) {
        const x = ledPositions[i][0];
        const y = ledPositions[i][1];
        const color = LightingMode === "Forced" ? hexToRgb(forcedColor) : device.color(x, y);
        packet.push(color[0], color[1], color[2]);
    }

    socket.send(packet);

    const now = Date.now();
    if (now - lastLog > 5000) {
        lastLog = now;
        device.log("AmpUp bridge frame sent");
    }
}

export function Shutdown() {
    if (socket) {
        socket.close();
        socket = undefined;
    }
}

function hexToRgb(hex) {
    const result = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(hex || "#000000");
    if (!result) return [0, 0, 0];
    return [parseInt(result[1], 16), parseInt(result[2], 16), parseInt(result[3], 16)];
}

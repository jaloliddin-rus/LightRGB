// @lightrgb-managed-plugin v2
export function Name() { return "MonsGeek Keyboard"; }
export function VendorId() { return 0x3151; }
export function ProductId() { return [0x5029, 0x5026, 0x502F, 0x502D]; }
export function Publisher() { return "LightRGB Community"; }
export function Documentation() { return "troubleshooting/monsgeek"; }
export function Size() { return [1, 1]; }
export function Type() { return "Hid"; }
export function DefaultPosition() { return [0, 0]; }
export function DefaultScale() { return 1.0; }
export function DeviceType() { return "keyboard"; }
export function ImageUrl() { return ""; }

export function Validate(endpoint) {
	return endpoint.interface === 2 && endpoint.usage_page === 0xffff && endpoint.usage === 0x0002;
}

/* global
shutdownColor:readonly
LightingMode:readonly
forcedColor:readonly
keyboardMode:readonly
keyboardBrightness:readonly
protocolFlag:readonly
*/
export function ControllableParameters() {
	return [
		{ property: "shutdownColor", group: "lighting", label: "Shutdown Color", description: "Color applied when LightRGB exits", min: "0", max: "360", type: "color", default: "#000000" },
		{ property: "LightingMode", group: "lighting", label: "Lighting Mode", description: "Canvas pulls from active effect; Forced uses Forced Color", type: "combobox", values: ["Canvas", "Forced"], default: "Canvas" },
		{ property: "forcedColor", group: "lighting", label: "Forced Color", description: "Color used when Forced Lighting Mode is enabled", min: "0", max: "360", type: "color", default: "#009bde" },
		{ property: "keyboardMode", group: "lighting", label: "Keyboard Mode", description: "Direct Sync streams smooth volatile 0x0E frames; other modes use onboard 0x07 presets", type: "combobox", values: ["Direct Sync (Smooth)", "Static (0x07)", "Off", "Breathing", "Neon", "Wave", "Rainbow"], default: "Direct Sync (Smooth)" },
		{ property: "keyboardBrightness", group: "lighting", label: "Keyboard Brightness (0-4)", description: "Hardware brightness level 0..4", type: "number", min: "0", max: "4", step: "1", default: "4" },
		{ property: "protocolFlag", group: "lighting", label: "0x07 Color Flag", description: "Used in Static/Onboard 0x07 modes (0x07 = single color, 0x08 = dazzle/alt)", type: "combobox", values: ["0x07 (Single Color)", "0x08 (Dazzle / Alt)"], default: "0x07 (Single Color)" }
	];
}

export function LedNames() { return ["Keyboard"]; }
export function LedPositions() { return [[0, 0]]; }

let directModeActive = false;
let lastDirectBright = -1;
let lastDirectRgbSig = "";
let lastLedParamSig = "";
let lastLedParamSendMs = 0;
const MIN_LEDPARAM_INTERVAL_MS = 150;

export function Initialize() {
	directModeActive = false;
	lastDirectBright = -1;
	lastDirectRgbSig = "";
	lastLedParamSig = "";
	lastLedParamSendMs = 0;
	sendColors(undefined, true);
}

export function Render() {
	sendColors(undefined, false);
}

export function Shutdown(SystemSuspending) {
	const target = SystemSuspending ? "#000000" : shutdownColor;
	sendColors(target, true, true);
}

export function onkeyboardModeChanged() { sendColors(undefined, true); }
export function onkeyboardBrightnessChanged() { sendColors(undefined, true); }
export function onprotocolFlagChanged() { sendColors(undefined, true); }
export function onLightingModeChanged() { sendColors(undefined, true); }
export function onforcedColorChanged() { sendColors(undefined, true); }

function sendColors(overrideColor, forceImmediate, isShutdown = false) {
	let modeName = typeof keyboardMode === "string" ? keyboardMode : "Direct Sync (Smooth)";
	let color;
	if (overrideColor) {
		color = hexToRgb(overrideColor);
	} else if (LightingMode === "Forced") {
		color = hexToRgb(forcedColor);
	} else {
		color = device.color(0, 0);
	}

	const bright = Math.max(0, Math.min(4, Number(keyboardBrightness ?? 4) | 0));
	const r = color[0] & 0xff;
	const g = color[1] & 0xff;
	const b = color[2] & 0xff;

	if (modeName === "Direct Sync (Smooth)") {
		ensureDirectMode(bright);
		const rgbSig = `${r},${g},${b}`;
		if (!forceImmediate && rgbSig === lastDirectRgbSig) {
			return;
		}
		lastDirectRgbSig = rgbSig;
		sendScreenColor(r, g, b);
		return;
	}

	directModeActive = false;
	lastDirectBright = -1;
	lastDirectRgbSig = "";

	if (isShutdown && overrideColor && overrideColor.toLowerCase() === "#000000") {
		modeName = "Off";
	}

	const modeByte = modeToByte(modeName);
	let effectiveBright = bright;
	if (modeByte === 0x00 || (r === 0 && g === 0 && b === 0 && modeByte === 0x01)) {
		effectiveBright = 0;
	}
	const flagByte = (typeof protocolFlag === "string" && protocolFlag.startsWith("0x08")) ? 0x08 : 0x07;
	sendLedParam(modeByte, effectiveBright, flagByte, r, g, b, forceImmediate);
}

function ensureDirectMode(bright) {
	if (directModeActive && lastDirectBright === bright) {
		return;
	}

	const packet = buildLedParamPacket(0x15, 0x04, bright, 0x07, 0x00, 0x00, 0x00);
	device.send_report(packet, 65);
	device.pause(15);

	directModeActive = true;
	lastDirectBright = bright;
	lastDirectRgbSig = "";
	lastLedParamSig = "";
}

function sendScreenColor(r, g, b) {
	const packet = new Array(65).fill(0);
	packet[0] = 0x00;
	packet[1] = 0x0E;
	packet[2] = r & 0xff;
	packet[3] = g & 0xff;
	packet[4] = b & 0xff;

	let sum = 0;
	for (let i = 1; i <= 7; i++) sum += packet[i];
	packet[8] = (0xff - (sum & 0xff)) & 0xff;

	device.send_report(packet, 65);
}

function sendLedParam(modeByte, bright, flagByte, r, g, b, forceImmediate) {
	const packet = buildLedParamPacket(modeByte, 0x04, bright, flagByte, r, g, b);
	const sig = `${packet[2]},${packet[4]},${packet[5]},${packet[6]},${packet[7]},${packet[8]}`;
	if (sig === lastLedParamSig) return;

	const now = Date.now();
	if (!forceImmediate && (now - lastLedParamSendMs) < MIN_LEDPARAM_INTERVAL_MS) {
		return;
	}

	lastLedParamSig = sig;
	lastLedParamSendMs = now;
	device.send_report(packet, 65);
}

function buildLedParamPacket(modeByte, speed, bright, flagByte, r, g, b) {
	const packet = new Array(65).fill(0);
	packet[0] = 0x00;
	packet[1] = 0x07;
	packet[2] = modeByte & 0xff;
	packet[3] = speed & 0xff;
	packet[4] = bright & 0xff;
	packet[5] = flagByte & 0xff;
	packet[6] = r & 0xff;
	packet[7] = g & 0xff;
	packet[8] = b & 0xff;

	let sum = 0;
	for (let i = 1; i <= 8; i++) sum += packet[i];
	packet[9] = (0xff - (sum & 0xff)) & 0xff;
	return packet;
}

function modeToByte(mode) {
	switch (mode) {
		case "Off": return 0x00;
		case "Breathing": return 0x02;
		case "Neon": return 0x03;
		case "Wave": return 0x04;
		case "Rainbow": return 0x10;
		default: return 0x01; // Static (0x07)
	}
}

function hexToRgb(hex) {
	const result = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(hex || "#009bde");
	if (!result) return [0, 155, 222];
	return [parseInt(result[1], 16), parseInt(result[2], 16), parseInt(result[3], 16)];
}

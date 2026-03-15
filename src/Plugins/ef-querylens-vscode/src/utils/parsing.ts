import { Uri } from 'vscode';

export function parseUri(uriInput: unknown): Uri | null {
    if (typeof uriInput !== 'string' || uriInput.length === 0) {
        return null;
    }

    try {
        return Uri.parse(uriInput);
    } catch {
        return null;
    }
}

export function clamp(value: number, min: number, max: number): number {
    if (!Number.isFinite(value)) {
        return min;
    }

    return Math.min(max, Math.max(min, value));
}

export function coerceNonNegativeInt(value: unknown, fallback: number): number {
    if (typeof value === 'number' && Number.isFinite(value)) {
        return Math.max(0, Math.floor(value));
    }

    if (typeof value === 'string') {
        const trimmed = value.trim();
        if (trimmed.length === 0) {
            return fallback;
        }

        const parsed = Number(trimmed);
        if (Number.isFinite(parsed)) {
            return Math.max(0, Math.floor(parsed));
        }
    }

    return fallback;
}


import { describe, expect, test } from 'vitest';

import { formatLogMessage, formatUserMessage } from '../src/utils/errors';

describe('error formatting', () => {
    test('formats user messages with code prefix', () => {
        expect(formatUserMessage('QL1002_INVALID_URI', 'uri is missing')).toBe(
            'EF QueryLens (QL1002_INVALID_URI): uri is missing'
        );
    });

    test('formats log messages with stable machine-friendly format', () => {
        expect(formatLogMessage('QL1006_DAEMON_RESTART_FAILED', 'timeout')).toBe(
            '[EFQueryLens][QL1006_DAEMON_RESTART_FAILED] timeout'
        );
    });
});

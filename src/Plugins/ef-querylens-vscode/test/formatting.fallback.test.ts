import { afterEach, describe, expect, test, vi } from 'vitest';

describe('sql formatting fallback', () => {
    afterEach(() => {
        vi.resetModules();
        vi.doUnmock('sql-formatter');
    });

    test('uses legacy formatter when sql-formatter throws', async () => {
        vi.doMock('sql-formatter', () => ({
            format: () => {
                throw new Error('forced parser error');
            },
        }));

        const { formatSql } = await import('../src/sql/formatting');
        const output = formatSql('select * from users where id = 1 order by id', 'sql');

        expect(output.toLowerCase()).toContain('\nwhere');
        expect(output.toLowerCase()).toContain('\norder by');
    });
});

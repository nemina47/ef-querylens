import { describe, expect, test } from 'vitest';

import { formatSql } from '../src/sql/formatting';

describe('sql formatting', () => {
    test('formats multi-query previews with explicit marker separator', () => {
        const input = 'select 1\n-- next query --\nselect 2';

        const output = formatSql(input, 'sql');

        expect(output).toContain('-- next query --');
        expect(output.toLowerCase()).toContain('select');
    });

    test('formats split-query previews while preserving split labels', () => {
        const input = [
            '-- ===== Split Query 1 of 2 =====',
            'select * from users where id = 1',
            '',
            '-- ===== Split Query 2 of 2 =====',
            'select * from posts where user_id = 1',
        ].join('\n');

        const output = formatSql(input, 'sql');

        expect(output).toContain('-- ===== Split Query 1 of 2 =====');
        expect(output).toContain('-- ===== Split Query 2 of 2 =====');
        expect(output.toLowerCase()).toContain('select');
    });

    test('auto dialect recognizes mysql style quoted identifiers', () => {
        const input = 'select `id`, `name` from `users`';

        const output = formatSql(input, 'auto');

        expect(output).toContain('`id`');
        expect(output.toLowerCase()).toContain('from');
    });

    test('auto dialect recognizes sql server bracketed identifiers', () => {
        const input = 'select [Id], [Name] from [Users]';

        const output = formatSql(input, 'auto');

        expect(output).toContain('[Id]');
        expect(output.toLowerCase()).toContain('from');
    });

    test('returns empty string when input is only whitespace', () => {
        expect(formatSql('   \n  ', 'sql')).toBe('');
    });
});

import path from 'node:path';
import { defineConfig } from 'vitest/config';

export default defineConfig({
    resolve: {
        alias: {
            vscode: path.resolve(__dirname, 'test/mocks/vscode.ts'),
            'vscode-languageclient/node': path.resolve(__dirname, 'test/mocks/vscode-languageclient-node.ts'),
        },
    },
    test: {
        include: ['test/**/*.test.ts'],
        environment: 'node',
        coverage: {
            provider: 'v8',
            reporter: ['text', 'cobertura'],
            include: ['src/**/*.ts'],
        },
    },
});

export const State = {
    Starting: 1,
    Running: 2,
    Stopped: 3,
};

type NotificationEntry = { method: string; params: unknown };

const instances: LanguageClient[] = [];

export type ServerOptions = unknown;
export type LanguageClientOptions = unknown;

export class LanguageClient {
    public state = State.Stopped;
    public readonly notifications: NotificationEntry[] = [];

    constructor(
        public readonly id: string,
        public readonly name: string,
        public readonly serverOptions: ServerOptions,
        public readonly clientOptions: LanguageClientOptions
    ) {
        instances.push(this);
    }

    start(): void {
        this.state = State.Running;
    }

    stop(): Promise<void> {
        this.state = State.Stopped;
        return Promise.resolve();
    }

    isRunning(): boolean {
        return this.state === State.Running;
    }

    sendNotification(method: string, params: unknown): Promise<void> {
        this.notifications.push({ method, params });
        return Promise.resolve();
    }

    sendRequest<T = unknown>(_method: string, _params?: unknown): Promise<T> {
        return Promise.reject(new Error('sendRequest not configured in LanguageClient mock'));
    }
}

export function getLanguageClientInstances(): LanguageClient[] {
    return [...instances];
}

export function resetLanguageClientMocks(): void {
    instances.length = 0;
}

type OpfsStepResult = {
    key: string;
    name: string;
    passed: boolean;
    message: string;
};

type OpfsSuiteResult = {
    key: string;
    name: string;
    supported: boolean;
    passed: boolean;
    message: string;
    steps: OpfsStepResult[];
};

type OpfsPocResult = {
    isSuccess: boolean;
    summary: string;
    completedAtUtc: string;
    suites: OpfsSuiteResult[];
};

type SyncSuiteWorkerRequest = {
    requestId: number;
    kind: "runSyncSuite";
};

type SyncSuiteWorkerResponse = {
    requestId: number;
    suite?: OpfsSuiteResult;
    error?: string;
};

type OpfsStorageManager = StorageManager & {
    getDirectory?: () => Promise<FileSystemDirectoryHandle>;
};

class UnsupportedFeatureError extends Error {
    constructor(message: string) {
        super(message);
        this.name = "UnsupportedFeatureError";
    }
}

const opfsRootDirectoryName = "synqra-opfs-poc";
const asyncFileName = "async-suite.txt";

export async function runOpfsPoc(): Promise<OpfsPocResult> {
    const suites = [
        await runAsyncSuite(),
        await runSyncSuiteViaWorker()
    ];

    const isSuccess = suites.every(suite => suite.passed);
    const summary = isSuccess
        ? "All OPFS PoC suites passed."
        : suites.some(suite => !suite.supported)
            ? "OPFS PoC completed, but some browser features are unsupported."
            : "One or more OPFS PoC suites failed.";

    return {
        isSuccess,
        summary,
        completedAtUtc: new Date().toISOString(),
        suites
    };
}

async function runAsyncSuite(): Promise<OpfsSuiteResult> {
    const steps: OpfsStepResult[] = [];

    try {
        const storageManager = navigator.storage as OpfsStorageManager;
        const rootDirectory = await runCheckedStep(
            steps,
            "detect-support",
            "Detect OPFS support",
            async () => {
                if (typeof storageManager.getDirectory !== "function") {
                    throw new UnsupportedFeatureError("navigator.storage.getDirectory is not available in this browser.");
                }

                return {
                    value: await storageManager.getDirectory(),
                    message: "navigator.storage.getDirectory is available."
                };
            }
        );

        const suiteDirectory = await runCheckedStep(
            steps,
            "open-directory",
            "Create or open PoC directory",
            async () => ({
                value: await rootDirectory.getDirectoryHandle(opfsRootDirectoryName, { create: true }),
                message: `Directory '${opfsRootDirectoryName}' is ready.`
            })
        );

        await deleteIfExists(suiteDirectory, asyncFileName);

        const fileHandle = await runCheckedStep(
            steps,
            "create-file",
            "Create async test file",
            async () => ({
                value: await suiteDirectory.getFileHandle(asyncFileName, { create: true }),
                message: `File '${asyncFileName}' is ready.`
            })
        );

        await runCheckedStep(
            steps,
            "write-hello",
            "Write initial content",
            async () => {
                const writable = await fileHandle.createWritable();
                try {
                    await writable.write("hello");
                } finally {
                    await writable.close();
                }

                return { value: undefined, message: "Wrote 'hello' to the file." };
            }
        );

        await runCheckedStep(
            steps,
            "reopen-read-hello",
            "Reopen and read initial content",
            async () => {
                const text = await readFileText(fileHandle);
                ensureText(text, "hello", "Expected the reopened file to contain 'hello'.");
                return { value: undefined, message: `Read back '${text}'.` };
            }
        );

        await runCheckedStep(
            steps,
            "append-world",
            "Append more content",
            async () => {
                const existingFile = await fileHandle.getFile();
                const writable = await fileHandle.createWritable({ keepExistingData: true });
                try {
                    await writable.write({
                        type: "write",
                        position: existingFile.size,
                        data: " world"
                    } as FileSystemWriteChunkType);
                } finally {
                    await writable.close();
                }

                return { value: undefined, message: "Appended ' world' to the file." };
            }
        );

        await runCheckedStep(
            steps,
            "reopen-read-full",
            "Reopen and verify appended content",
            async () => {
                const text = await readFileText(fileHandle);
                ensureText(text, "hello world", "Expected the reopened file to contain 'hello world'.");
                return { value: undefined, message: `Read back '${text}'.` };
            }
        );

        await runCheckedStep(
            steps,
            "enumerate-present",
            "Enumerate directory with file present",
            async () => {
                const names = await listEntryNames(suiteDirectory);
                if (!names.includes(asyncFileName)) {
                    throw new Error(`Directory listing does not contain '${asyncFileName}'.`);
                }

                return { value: undefined, message: `Found '${asyncFileName}' in the directory.` };
            }
        );

        await runCheckedStep(
            steps,
            "delete-file",
            "Delete async test file",
            async () => {
                await suiteDirectory.removeEntry(asyncFileName);
                return { value: undefined, message: `Deleted '${asyncFileName}'.` };
            }
        );

        await runCheckedStep(
            steps,
            "enumerate-absent",
            "Enumerate directory after delete",
            async () => {
                const names = await listEntryNames(suiteDirectory);
                if (names.includes(asyncFileName)) {
                    throw new Error(`Directory listing still contains '${asyncFileName}'.`);
                }

                return { value: undefined, message: `Confirmed '${asyncFileName}' is gone.` };
            }
        );

        return createSuiteResult("async", "Async OPFS API", true, true, "All async OPFS checks passed.", steps);
    } catch (error) {
        return createSuiteResult(
            "async",
            "Async OPFS API",
            !(error instanceof UnsupportedFeatureError),
            false,
            getErrorMessage(error),
            steps
        );
    }
}

async function runSyncSuiteViaWorker(): Promise<OpfsSuiteResult> {
    const bootstrapSteps: OpfsStepResult[] = [];
    let worker: Worker | null = null;

    try {
        worker = await runCheckedStep(
            bootstrapSteps,
            "start-worker",
            "Start dedicated worker",
            async () => ({
                value: new Worker(new URL("./opfsSyncWorker.js", import.meta.url), { type: "module" }),
                message: "Dedicated worker is running."
            })
        );

        const suite = await runWorkerSyncSuite(worker);
        suite.steps = [...bootstrapSteps, ...suite.steps];
        return suite;
    } catch (error) {
        const supported = !(error instanceof UnsupportedFeatureError);
        return createSuiteResult(
            "sync-worker",
            "Sync Access Handle Worker",
            supported,
            false,
            getErrorMessage(error),
            bootstrapSteps
        );
    } finally {
        worker?.terminate();
    }
}

async function runWorkerSyncSuite(worker: Worker): Promise<OpfsSuiteResult> {
    return await new Promise<OpfsSuiteResult>((resolve, reject) => {
        const requestId = Date.now();
        const timeoutId = window.setTimeout(() => {
            cleanup();
            reject(new Error("Timed out while waiting for the sync access handle worker."));
        }, 30000);

        const cleanup = (): void => {
            window.clearTimeout(timeoutId);
            worker.removeEventListener("message", onMessage);
            worker.removeEventListener("error", onError);
            worker.removeEventListener("messageerror", onMessageError);
        };

        const onMessage = (event: MessageEvent<SyncSuiteWorkerResponse>): void => {
            if (!event.data || event.data.requestId !== requestId) {
                return;
            }

            cleanup();

            if (event.data.error) {
                reject(new Error(event.data.error));
                return;
            }

            if (!event.data.suite) {
                reject(new Error("Worker completed without returning a suite result."));
                return;
            }

            resolve(event.data.suite);
        };

        const onError = (event: ErrorEvent): void => {
            cleanup();
            reject(new Error(event.message || "The sync access handle worker raised an error."));
        };

        const onMessageError = (): void => {
            cleanup();
            reject(new Error("The sync access handle worker could not deserialize a message."));
        };

        worker.addEventListener("message", onMessage);
        worker.addEventListener("error", onError);
        worker.addEventListener("messageerror", onMessageError);

        const request: SyncSuiteWorkerRequest = {
            requestId,
            kind: "runSyncSuite"
        };

        worker.postMessage(request);
    });
}

async function runCheckedStep<T>(
    steps: OpfsStepResult[],
    key: string,
    name: string,
    action: () => Promise<{ value: T; message: string }>
): Promise<T> {
    try {
        const result = await action();
        steps.push({
            key,
            name,
            passed: true,
            message: result.message
        });
        return result.value;
    } catch (error) {
        steps.push({
            key,
            name,
            passed: false,
            message: getErrorMessage(error)
        });
        throw error;
    }
}

async function readFileText(fileHandle: FileSystemFileHandle): Promise<string> {
    const file = await fileHandle.getFile();
    return await file.text();
}

async function listEntryNames(directoryHandle: FileSystemDirectoryHandle): Promise<string[]> {
    const names: string[] = [];
    const iterableHandle = directoryHandle as FileSystemDirectoryHandle & {
        entries?: () => AsyncIterable<[string, FileSystemHandle]>;
        keys?: () => AsyncIterable<string>;
    };

    if (typeof iterableHandle.keys === "function") {
        for await (const name of iterableHandle.keys()) {
            names.push(name);
        }
        return names;
    }

    if (typeof iterableHandle.entries === "function") {
        for await (const [name] of iterableHandle.entries()) {
            names.push(name);
        }
        return names;
    }

    throw new Error("The browser does not expose directory iteration APIs for OPFS.");
}

async function deleteIfExists(directoryHandle: FileSystemDirectoryHandle, entryName: string): Promise<void> {
    try {
        await directoryHandle.removeEntry(entryName);
    } catch (error) {
        const domError = error as DOMException | undefined;
        if (domError?.name !== "NotFoundError") {
            throw error;
        }
    }
}

function ensureText(actual: string, expected: string, message: string): void {
    if (actual !== expected) {
        throw new Error(`${message} Actual: '${actual}'.`);
    }
}

function createSuiteResult(
    key: string,
    name: string,
    supported: boolean,
    passed: boolean,
    message: string,
    steps: OpfsStepResult[]
): OpfsSuiteResult {
    return {
        key,
        name,
        supported,
        passed,
        message,
        steps
    };
}

function getErrorMessage(error: unknown): string {
    if (error instanceof Error) {
        return error.message;
    }

    return typeof error === "string" ? error : JSON.stringify(error);
}

const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const bridgePath = process.argv[2];
if (!bridgePath) throw new Error('Expected path to nexmud-editor-bridge.js');
const source = fs.readFileSync(bridgePath, 'utf8');

function createBridge({ transport, immediateTimeout = false } = {}) {
  const messages = [];
  const modes = [];
  const providers = new Map();
  let timerId = 0;
  const defaults = {
    setModeConfiguration: configuration => modes.push(configuration),
    setDiagnosticsOptions() {},
    addExtraLib() { return { dispose() {} }; }
  };
  const editor = {
    updateOptions() {}, addCommand() {}, onDidChangeCursorSelection() { return { dispose() {} }; },
    getModel() { return null; }, setModel() {}, dispose() {}
  };
  const monaco = {
    editor: {
      setTheme() {}, create: () => editor, getModel: () => null, setModelMarkers() {}
    },
    languages: {
      typescript: { typescriptDefaults: defaults, javascriptDefaults: defaults },
      registerCompletionItemProvider(language, provider) { providers.set(`${language.language}:completion`, provider); return { dispose() {} }; },
      registerHoverProvider(language, provider) { providers.set(`${language.language}:hover`, provider); return { dispose() {} }; },
      registerSignatureHelpProvider(language, provider) { providers.set(`${language.language}:signature`, provider); return { dispose() {} }; },
      registerDefinitionProvider(language, provider) { providers.set(`${language.language}:definition`, provider); return { dispose() {} }; },
      registerTypeDefinitionProvider(language, provider) { providers.set(`${language.language}:typeDefinition`, provider); return { dispose() {} }; },
      registerReferenceProvider(language, provider) { providers.set(`${language.language}:references`, provider); return { dispose() {} }; },
      registerRenameProvider(language, provider) { providers.set(`${language.language}:rename`, provider); return { dispose() {} }; },
      registerDocumentSymbolProvider(language, provider) { providers.set(`${language.language}:symbols`, provider); return { dispose() {} }; }
    },
    Range: class { constructor(...args) { this.args = args; } },
    MarkerSeverity: { Error: 8, Warning: 4, Info: 2, Hint: 1 },
    Uri: { parse: value => ({ toString: () => value }) },
    KeyMod: { CtrlCmd: 1, Shift: 2 }, KeyCode: { KeyS: 3 }
  };
  const context = {
    monaco,
    document: { getElementById: () => ({ style: {}, textContent: '' }) },
    require: Object.assign((_, ready) => ready(), { config() {} }),
    addEventListener() {},
    invokeCSharpAction: value => {
      if (transport) return transport(value);
      messages.push(JSON.parse(value));
    },
    setTimeout: (callback, delay) => {
      const id = ++timerId;
      if (immediateTimeout && delay === 25000) queueMicrotask(callback);
      return id;
    },
    clearTimeout() {},
    console
  };
  context.globalThis = context;
  vm.runInNewContext(source, context, { filename: bridgePath });
  const bridge = context.nexmudEditorBridge;
  const receive = (type, payload = {}) => bridge.receive({ version: 1, type, payload });
  receive('setLanguageServerAvailable', { available: true });
  return { bridge, receive, messages, modes, providers };
}

async function requestError(errorPayload) {
  const instance = createBridge();
  const initialModeCount = instance.modes.length;
  const provider = instance.providers.get('typescript:hover');
  const pending = provider.provideHover({ uri: { toString: () => 'file:///main.ts' } }, { lineNumber: 1, column: 1 });
  const message = instance.messages.find(item => item.type === 'lspRequest');
  assert.ok(message, 'provider should dispatch an LSP request');
  instance.receive('lspResponse', { requestId: message.payload.requestId, error: errorPayload });
  assert.equal(await pending, null, 'ordinary LSP errors should resolve with Monaco’s empty hover result');
  const followup = provider.provideHover({ uri: { toString: () => 'file:///main.ts' } }, { lineNumber: 1, column: 1 });
  const followupMessage = instance.messages.filter(item => item.type === 'lspRequest').at(-1);
  assert.ok(followupMessage, 'JSON-RPC errors must leave the language server available');
  instance.receive('lspResponse', { requestId: followupMessage.payload.requestId, result: null });
  assert.equal(await followup, null);
  assert.equal(instance.modes.length, initialModeCount, 'ordinary request errors must not activate Monaco fallback');
}

async function resolveHoverWithMonaco(instance) {
  const provider = instance.providers.get('typescript:hover');
  const lspResult = await provider.provideHover(
    { uri: { toString: () => 'file:///main.ts' } }, { lineNumber: 1, column: 1 });
  if (lspResult) return lspResult;
  return instance.modes.at(-1).hovers
    ? { contents: [{ value: 'browser TypeScript worker hover' }] }
    : null;
}

async function main() {
  const available = createBridge();
  for (const feature of ['documentHighlights', 'onTypeFormattingEdits', 'codeActions', 'inlayHints', 'documentRangeFormattingEdits'])
    assert.equal(available.modes.at(-1)[feature], true, `${feature} should remain provided by Monaco`);

  await requestError({ code: -32602, message: 'Invalid params', data: { detail: 'bad position' }, unavailable: false });
  await requestError('ordinary request error');

  let transportRequests = 0;
  const transportFailure = createBridge({ transport: value => {
    if (JSON.parse(value).type === 'lspRequest') {
      transportRequests++;
      throw new Error('webview disconnected');
    }
  } });
  const transportModeCount = transportFailure.modes.length;
  await transportFailure.providers.get('typescript:hover')
    .provideHover({ uri: { toString: () => 'file:///main.ts' } }, { lineNumber: 1, column: 1 });
  await transportFailure.providers.get('typescript:hover')
    .provideHover({ uri: { toString: () => 'file:///main.ts' } }, { lineNumber: 1, column: 1 });
  assert.equal(transportRequests, 1, 'transport failure should mark the language service unavailable');
  assert.equal(transportFailure.modes.length, transportModeCount + 2);
  assert.equal(transportFailure.modes.at(-1).hovers, true, 'transport failure should activate Monaco fallback');
  assert.equal((await resolveHoverWithMonaco(transportFailure)).contents[0].value,
    'browser TypeScript worker hover', 'a hover should resolve through Monaco after fallback is re-enabled');

  const timeout = createBridge({ immediateTimeout: true });
  const timeoutModeCount = timeout.modes.length;
  await timeout.providers.get('typescript:hover')
    .provideHover({ uri: { toString: () => 'file:///main.ts' } }, { lineNumber: 1, column: 1 });
  await timeout.providers.get('typescript:hover')
    .provideHover({ uri: { toString: () => 'file:///main.ts' } }, { lineNumber: 1, column: 1 });
  assert.equal(timeout.messages.filter(item => item.type === 'lspRequest').length, 1,
    'bridge timeout should mark the language service unavailable');
  assert.equal(timeout.modes.length, timeoutModeCount + 2);
  assert.equal(timeout.modes.at(-1).hovers, true, 'bridge timeout should activate Monaco fallback');
  assert.equal((await resolveHoverWithMonaco(timeout)).contents[0].value,
    'browser TypeScript worker hover', 'a hover should resolve through Monaco after timeout fallback is re-enabled');

  console.log('Monaco bridge request/state tests passed (errors, cancellation, fallback recovery).');
}

main().catch(error => { console.error(error); process.exitCode = 1; });

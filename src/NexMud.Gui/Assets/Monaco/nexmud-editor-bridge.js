(() => {
  'use strict';
  const protocolVersion = 1;
  const models = new Map();
  const disposables = [];
  let editor = null;
  let readOnly = false;
  let ready = false;

  function send(type, payload = {}) {
    if (typeof globalThis.invokeCSharpAction !== 'function') return;
    globalThis.invokeCSharpAction(JSON.stringify({ version: protocolVersion, type, payload }));
  }

  function fatal(message) {
    const target = document.getElementById('fatal');
    target.style.display = 'block';
    target.textContent = String(message ?? 'Editor initialization failed.');
    if (editor) editor.updateOptions({ readOnly: true });
    send('editorFailure', { message: target.textContent });
  }

  function requireModel(uriText) {
    const model = models.get(uriText);
    if (!model) throw new Error(`Unknown editor model: ${uriText}`);
    return model;
  }

  function languageFor(path) {
    const lower = String(path ?? '').toLowerCase();
    if (lower.endsWith('.ts') || lower.endsWith('.d.ts')) return 'typescript';
    if (lower.endsWith('.js')) return 'javascript';
    if (lower.endsWith('.json')) return 'json';
    return 'plaintext';
  }

  async function requestSymbols(uriText, requestId) {
    const model = requireModel(uriText);
    try {
      const workerFactory = model.getLanguageId() === 'javascript'
        ? monaco.languages.typescript.getJavaScriptWorker
        : monaco.languages.typescript.getTypeScriptWorker;
      const worker = await workerFactory();
      const client = await worker(model.uri);
      const tree = await client.getNavigationTree(model.uri.toString());
      send('editorCommandInvoked', { command: 'symbolsResult', requestId, uri: uriText, symbols: tree ?? null });
    } catch (error) {
      send('editorCommandInvoked', { command: 'symbolsResult', requestId, uri: uriText, error: error?.message ?? String(error) });
    }
  }

  const handlers = {
    initialize(payload) {
      if (payload?.version !== protocolVersion) {
        fatal(`Editor bridge protocol mismatch. Host=${payload?.version ?? 'unknown'} Editor=${protocolVersion}`);
        return;
      }
      if (payload.theme) monaco.editor.setTheme(payload.theme);
      if (payload.fontFamily || payload.fontSize) {
        editor.updateOptions({
          fontFamily: payload.fontFamily || undefined,
          fontSize: Number(payload.fontSize) || undefined
        });
      }
    },
    openDocument(payload) {
      const uriText = String(payload.uri);
      let model = monaco.editor.getModel(monaco.Uri.parse(uriText));
      if (!model) {
        model = monaco.editor.createModel(String(payload.content ?? ''), languageFor(payload.path), monaco.Uri.parse(uriText));
        const subscription = model.onDidChangeContent(() => {
          send('documentChanged', { uri: uriText, versionId: model.getVersionId(), alternativeVersionId: model.getAlternativeVersionId() });
        });
        disposables.push(subscription);
      } else if (model.getValue() !== String(payload.content ?? '')) {
        model.setValue(String(payload.content ?? ''));
      }
      models.set(uriText, model);
      editor.setModel(model);
      editor.updateOptions({ readOnly: Boolean(payload.readOnly ?? readOnly) });
      send('activeDocumentChanged', { uri: uriText });
    },
    closeDocument(payload) {
      const uriText = String(payload.uri);
      const model = models.get(uriText);
      models.delete(uriText);
      if (editor.getModel() === model) editor.setModel(null);
      model?.dispose();
    },
    setActiveDocument(payload) {
      const uriText = String(payload.uri);
      editor.setModel(requireModel(uriText));
      send('activeDocumentChanged', { uri: uriText });
    },
    setDocumentContent(payload) {
      const model = requireModel(String(payload.uri));
      const value = String(payload.content ?? '');
      if (model.getValue() !== value) model.setValue(value);
    },
    setReadOnly(payload) {
      readOnly = Boolean(payload.readOnly);
      editor.updateOptions({ readOnly });
    },
    revealLocation(payload) {
      const uriText = String(payload.uri);
      const model = requireModel(uriText);
      editor.setModel(model);
      const line = Math.max(1, Number(payload.line) || 1);
      const column = Math.max(1, Number(payload.column) || 1);
      editor.setPosition({ lineNumber: line, column });
      editor.revealPositionInCenter({ lineNumber: line, column });
      editor.focus();
    },
    focus() { editor.focus(); },
    setTheme(payload) { monaco.editor.setTheme(String(payload.theme || 'vs-dark')); },
    setFont(payload) {
      editor.updateOptions({ fontFamily: String(payload.family || ''), fontSize: Number(payload.size) || 13 });
    },
    registerSdkDeclarations(payload) {
      const content = String(payload.content ?? '');
      const uri = String(payload.uri ?? 'file:///nexmud-api.d.ts');
      monaco.languages.typescript.typescriptDefaults.addExtraLib(content, uri);
      monaco.languages.typescript.javascriptDefaults.addExtraLib(content, uri);
    },
    requestDocumentContent(payload) {
      const uriText = String(payload.uri);
      const model = requireModel(uriText);
      send('editorCommandInvoked', { command: 'documentContent', requestId: payload.requestId, uri: uriText, content: model.getValue() });
    },
    requestSymbols(payload) { void requestSymbols(String(payload.uri), payload.requestId); }
  };

  globalThis.nexmudEditorBridge = Object.freeze({
    receive(envelope) {
      if (!envelope || envelope.version !== protocolVersion) {
        fatal(`Editor bridge protocol mismatch. Host=${envelope?.version ?? 'unknown'} Editor=${protocolVersion}`);
        return;
      }
      const handler = handlers[envelope.type];
      if (!handler) {
        send('editorCommandInvoked', { command: 'unknownHostOperation', operation: String(envelope.type ?? '') });
        return;
      }
      try { handler(envelope.payload ?? {}); }
      catch (error) { fatal(error?.stack ?? error?.message ?? String(error)); }
    }
  });

  require.config({ paths: { vs: './vs' } });
  require(['vs/editor/editor.main'], () => {
    try {
      monaco.languages.typescript.typescriptDefaults.setCompilerOptions({
        target: monaco.languages.typescript.ScriptTarget.ES2022,
        module: monaco.languages.typescript.ModuleKind.ESNext,
        moduleResolution: monaco.languages.typescript.ModuleResolutionKind.Bundler,
        strict: true,
        allowNonTsExtensions: true,
        noEmit: true
      });
      editor = monaco.editor.create(document.getElementById('editor'), {
        automaticLayout: true,
        theme: 'vs-dark',
        fontSize: 13,
        minimap: { enabled: true },
        folding: true,
        multiCursorModifier: 'alt',
        renderWhitespace: 'selection',
        scrollBeyondLastLine: false,
        wordWrap: 'off',
        contextmenu: true,
        readOnly: false
      });
      editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
        const model = editor.getModel();
        if (model) send('saveRequested', { uri: model.uri.toString(), all: false });
      });
      editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyS, () => {
        send('saveRequested', { uri: editor.getModel()?.uri.toString() ?? null, all: true });
      });
      disposables.push(editor.onDidChangeCursorSelection(event => {
        const model = editor.getModel();
        if (!model) return;
        send('selectionChanged', {
          uri: model.uri.toString(),
          startLine: event.selection.startLineNumber,
          startColumn: event.selection.startColumn,
          endLine: event.selection.endLineNumber,
          endColumn: event.selection.endColumn
        });
      }));
      ready = true;
      send('editorReady', { version: protocolVersion });
    } catch (error) {
      fatal(error?.stack ?? error?.message ?? String(error));
    }
  }, error => fatal(error?.stack ?? error?.message ?? String(error)));

  globalThis.addEventListener('beforeunload', () => {
    for (const disposable of disposables.splice(0)) {
      try { disposable.dispose(); } catch { }
    }
    if (editor) editor.dispose();
    for (const model of models.values()) {
      try { model.dispose(); } catch { }
    }
    models.clear();
    ready = false;
  });
})();

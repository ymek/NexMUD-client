(() => {
  'use strict';
  const protocolVersion = 1;
  const models = new Map();
  const disposables = [];
  const modelSubscriptions = new Map();
  const lspRequests = new Map();
  let nextLspRequestId = 0;
  let editor = null;
  let readOnly = false;
  let languageServerAvailable = false;
  let sdkDeclarations = null;

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
    if (lower.endsWith('.ts') || lower.endsWith('.tsx') || lower.endsWith('.d.ts')) return 'typescript';
    if (lower.endsWith('.js') || lower.endsWith('.jsx')) return 'javascript';
    if (lower.endsWith('.json')) return 'json';
    return 'plaintext';
  }

  function lspRequest(method, parameters) {
    if (!languageServerAvailable) return Promise.resolve(undefined);
    const requestId = String(++nextLspRequestId);
    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        lspRequests.delete(requestId);
        handlers.setLanguageServerAvailable({ available: false });
        reject(new Error('TypeScript language service request timed out.'));
      }, 25000);
      lspRequests.set(requestId, {
        resolve: value => { clearTimeout(timeout); resolve(value); },
        reject: error => { clearTimeout(timeout); reject(error); }
      });
      send('lspRequest', { requestId, method, parameters });
    }).catch(() => {
      handlers.setLanguageServerAvailable({ available: false });
      return undefined;
    });
  }

  function positionParams(model, position) {
    return { textDocument: { uri: model.uri.toString() }, position: { line: position.lineNumber - 1, character: position.column - 1 } };
  }

  function toRange(range) {
    if (!range) return undefined;
    return new monaco.Range(
      range.start.line + 1, range.start.character + 1,
      range.end.line + 1, range.end.character + 1);
  }

  function toMarkerSeverity(severity) {
    switch (severity) {
      case 1: return monaco.MarkerSeverity.Error;
      case 2: return monaco.MarkerSeverity.Warning;
      case 3: return monaco.MarkerSeverity.Info;
      default: return monaco.MarkerSeverity.Hint;
    }
  }

  function toMarkdown(value) {
    if (typeof value === 'string') return value;
    if (value == null) return '';
    return { value: typeof value.value === 'string' ? value.value : String(value) };
  }

  function toLocations(value) {
    if (!value) return value;
    const convert = item => {
      const uri = item.uri ?? item.targetUri;
      const range = item.range ?? item.targetSelectionRange ?? item.targetRange;
      return uri && range ? { uri: monaco.Uri.parse(uri), range: toRange(range) } : item;
    };
    return Array.isArray(value) ? value.map(convert) : convert(value);
  }

  function toDocumentSymbols(symbols) {
    return (symbols ?? []).map(symbol => ({
      ...symbol,
      range: toRange(symbol.range),
      selectionRange: toRange(symbol.selectionRange),
      children: symbol.children ? toDocumentSymbols(symbol.children) : undefined
    }));
  }

  function setBrowserTypeScriptProvidersEnabled(enabled) {
    const modeConfiguration = {
      completionItems: enabled,
      hovers: enabled,
      documentSymbols: enabled,
      definitions: enabled,
      references: enabled,
      rename: enabled,
      diagnostics: enabled,
      documentHighlights: enabled,
      onTypeFormattingEdits: enabled,
      codeActions: enabled,
      inlayHints: enabled,
      signatureHelp: enabled,
      documentRangeFormattingEdits: enabled
    };
    monaco.languages.typescript.typescriptDefaults.setModeConfiguration(modeConfiguration);
    monaco.languages.typescript.javascriptDefaults.setModeConfiguration(modeConfiguration);
    const diagnostics = { noSemanticValidation: !enabled, noSyntaxValidation: !enabled, noSuggestionDiagnostics: !enabled };
    monaco.languages.typescript.typescriptDefaults.setDiagnosticsOptions(diagnostics);
    monaco.languages.typescript.javascriptDefaults.setDiagnosticsOptions(diagnostics);
  }

  function registerLanguageProviders(languageId) {
    const selector = { language: languageId };
    disposables.push(monaco.languages.registerCompletionItemProvider(selector, {
      triggerCharacters: ['.', '/', '@'],
      provideCompletionItems: (model, position) => lspRequest('textDocument/completion', positionParams(model, position))
        .then(value => ({
          incomplete: Boolean(value?.isIncomplete),
          suggestions: (value?.items ?? value ?? []).map(item => {
            const textEdit = item.textEdit;
            const word = model.getWordUntilPosition(position);
            const range = toRange(textEdit?.range ?? textEdit?.replace ?? textEdit?.insert) ?? new monaco.Range(
              position.lineNumber, word.startColumn, position.lineNumber, word.endColumn);
            return {
              ...item,
              range,
              insertText: textEdit?.newText ?? item.insertText ?? item.label,
              insertTextRules: item.insertTextFormat === 2 ? monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet : undefined,
              textEdit: undefined,
              additionalTextEdits: item.additionalTextEdits?.map(edit => ({ range: toRange(edit.range), text: edit.newText }))
            };
          })
        })),
      resolveCompletionItem: item => lspRequest('completionItem/resolve', item).then(value => ({ ...item, ...value }))
    }));
    disposables.push(monaco.languages.registerHoverProvider(selector, {
      provideHover: (model, position) => lspRequest('textDocument/hover', positionParams(model, position))
        .then(value => {
          if (!value) return null;
          const contents = Array.isArray(value.contents) ? value.contents : [value.contents];
          return { contents: contents.map(toMarkdown), range: toRange(value.range) };
        })
    }));
    disposables.push(monaco.languages.registerSignatureHelpProvider(selector, {
      signatureHelpTriggerCharacters: ['(', ','],
      provideSignatureHelp: (model, position) => lspRequest('textDocument/signatureHelp', positionParams(model, position))
        .then(value => value ? {
          value: {
            ...value,
            signatures: (value.signatures ?? []).map(signature => ({
              ...signature,
              documentation: toMarkdown(signature.documentation),
              parameters: (signature.parameters ?? []).map(parameter => ({ ...parameter, documentation: toMarkdown(parameter.documentation) }))
            }))
          },
          dispose() {}
        } : null)
    }));
    disposables.push(monaco.languages.registerDefinitionProvider(selector, {
      provideDefinition: (model, position) => lspRequest('textDocument/definition', positionParams(model, position)).then(toLocations)
    }));
    disposables.push(monaco.languages.registerTypeDefinitionProvider(selector, {
      provideTypeDefinition: (model, position) => lspRequest('textDocument/typeDefinition', positionParams(model, position)).then(toLocations)
    }));
    disposables.push(monaco.languages.registerReferenceProvider(selector, {
      provideReferences: (model, position) => lspRequest('textDocument/references', { ...positionParams(model, position), context: { includeDeclaration: true } }).then(toLocations)
    }));
    disposables.push(monaco.languages.registerRenameProvider(selector, {
      provideRenameEdits: (model, position, newName) => lspRequest('textDocument/rename', { ...positionParams(model, position), newName })
        .then(edit => {
          if (!edit) return undefined;
          const changes = Object.entries(edit.changes ?? {}).flatMap(([uri, edits]) => edits.map(change => ({
            resource: monaco.Uri.parse(uri),
            textEdit: { range: toRange(change.range), text: change.newText }
          })));
          const documentChanges = (edit.documentChanges ?? []).flatMap(change =>
            (change.textDocument && change.edits ? change.edits : []).map(item => ({
              resource: monaco.Uri.parse(change.textDocument.uri),
              textEdit: { range: toRange(item.range), text: item.newText }
            })));
          return { edits: [...changes, ...documentChanges] };
        })
    }));
    disposables.push(monaco.languages.registerDocumentSymbolProvider(selector, {
      provideDocumentSymbols: model => lspRequest('textDocument/documentSymbol', { textDocument: { uri: model.uri.toString() } })
        .then(toDocumentSymbols)
    }));
  }

  function requestSymbols(uriText, requestId) {
    const model = requireModel(uriText);
    lspRequest('textDocument/documentSymbol', { textDocument: { uri: model.uri.toString() } })
      .then(symbols => send('editorCommandInvoked', { command: 'symbolsResult', requestId, uri: uriText, symbols }))
      .catch(error => send('editorCommandInvoked', { command: 'symbolsResult', requestId, uri: uriText, error: error?.message ?? String(error) }));
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
    registerSdkDeclarations(payload) {
      const content = String(payload.content ?? '');
      if (!content || content === sdkDeclarations) return;
      sdkDeclarations = content;
      const uri = 'file:///node_modules/@nexmud/api/index.d.ts';
      disposables.push(monaco.languages.typescript.typescriptDefaults.addExtraLib(content, uri));
      disposables.push(monaco.languages.typescript.javascriptDefaults.addExtraLib(content, uri));
    },
    openDocument(payload) {
      const uriText = String(payload.uri);
      let model = monaco.editor.getModel(monaco.Uri.parse(uriText));
      if (!model) {
        model = monaco.editor.createModel(String(payload.content ?? ''), languageFor(payload.path), monaco.Uri.parse(uriText));
        const subscription = model.onDidChangeContent(() => {
          send('documentChanged', { uri: uriText, versionId: model.getVersionId(), alternativeVersionId: model.getAlternativeVersionId(), text: model.getValue() });
        });
        modelSubscriptions.set(uriText, subscription);
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
      modelSubscriptions.get(uriText)?.dispose();
      modelSubscriptions.delete(uriText);
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
    lspResponse(payload) {
      const pending = lspRequests.get(String(payload.requestId));
      if (!pending) return;
      lspRequests.delete(String(payload.requestId));
      if (payload.error) pending.reject(new Error(String(payload.error)));
      else pending.resolve(payload.result);
    },
    setLanguageServerAvailable(payload) {
      languageServerAvailable = Boolean(payload.available);
      setBrowserTypeScriptProvidersEnabled(!languageServerAvailable);
      if (!languageServerAvailable) {
        for (const model of models.values()) monaco.editor.setModelMarkers(model, 'typescript-language-server', []);
      }
    },
    setDiagnostics(payload) {
      const model = models.get(String(payload.uri));
      if (!model) return;
      const markers = (payload.diagnostics ?? []).map(item => ({
        severity: toMarkerSeverity(item.severity),
        message: String(item.message ?? ''),
        source: item.source ?? 'TypeScript',
        code: item.code == null ? undefined : String(item.code),
        startLineNumber: (item.range?.start?.line ?? 0) + 1,
        startColumn: (item.range?.start?.character ?? 0) + 1,
        endLineNumber: (item.range?.end?.line ?? item.range?.start?.line ?? 0) + 1,
        endColumn: (item.range?.end?.character ?? item.range?.start?.character ?? 0) + 1
      }));
      monaco.editor.setModelMarkers(model, 'typescript-language-server', markers);
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
      setBrowserTypeScriptProvidersEnabled(true);
      registerLanguageProviders('typescript');
      registerLanguageProviders('javascript');
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
        handlers.setLanguageServerAvailable({ available: languageServerAvailable });
      send('editorReady', { version: protocolVersion });
    } catch (error) {
      fatal(error?.stack ?? error?.message ?? String(error));
    }
  }, error => fatal(error?.stack ?? error?.message ?? String(error)));

  globalThis.addEventListener('beforeunload', () => {
    for (const disposable of disposables.splice(0)) {
      try { disposable.dispose(); } catch { }
    }
    for (const pending of lspRequests.values()) pending.reject(new Error('Editor bridge closed.'));
    lspRequests.clear();
    modelSubscriptions.clear();
    if (editor) editor.dispose();
    for (const model of models.values()) {
      try { model.dispose(); } catch { }
    }
    models.clear();
  });
})();

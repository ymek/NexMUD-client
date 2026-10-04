using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NexMud.Client.Scripting;

namespace NexMud.Gui.AutomationStudio;

internal sealed partial class AutomationStudioWindow
{
    private sealed record TestResultRow(string PackageId, ScriptTestAssertionResult Assertion);
    private sealed record FailedTestGroup(string PackageId, IReadOnlyList<string> Files, IReadOnlyList<string> Names);

    private readonly StackPanel _testPanel = new() { Spacing = 6 };
    private readonly StackPanel _testFiles = new() { Spacing = 2 };
    private readonly StackPanel _testResults = new() { Spacing = 2 };
    private readonly TextBlock _testState = new() { Text = "Select a package to discover tests.", Foreground = UiTheme.Muted };
    private readonly List<TestResultRow> _testRows = [];
    private readonly List<FailedTestGroup> _failedTestGroups = [];
    private CancellationTokenSource? _testRunCancellation;
    private string? _selectedTestFile;
    private string? _selectedTestPackageId;
    private string? _selectedTestName;
    private long _testRunGeneration;

    private Control BuildTestsTab()
    {
        Button discover = TestButton("Discover");
        discover.Click += async (_, _) => await DiscoverTestsAsync().ConfigureAwait(true);
        Button all = TestButton("Run all packages");
        all.Click += async (_, _) => await RunTestsAsync(_packages.Select(package => package.Definition.PackageId).ToArray()).ConfigureAwait(true);
        Button package = TestButton("Run package");
        package.Click += async (_, _) =>
        {
            if (_activePackage is not null)
                await RunTestsAsync([_activePackage.Definition.PackageId]).ConfigureAwait(true);
        };
        Button file = TestButton("Run active file");
        file.Click += async (_, _) =>
        {
            if (_documents.Active is { IsScript: true, PackageId: { } packageId, Path: { } path })
                await RunTestsAsync([packageId], new Dictionary<string, IReadOnlyList<string>> { [packageId] = [path] }).ConfigureAwait(true);
            else _testState.Text = "Open a script test file first.";
        };
        Button selected = TestButton("Run selected test");
        selected.Click += async (_, _) =>
        {
            string? packageId = _selectedTestPackageId ?? _activePackage?.Definition.PackageId;
            string? selectedFile = _selectedTestFile;
            if (packageId is not null && (selectedFile is not null || _selectedTestName is not null))
            {
                IReadOnlyDictionary<string, IReadOnlyList<string>>? selectedFiles = selectedFile is null ? null :
                    new Dictionary<string, IReadOnlyList<string>> { [packageId] = [selectedFile] };
                IReadOnlyDictionary<string, IReadOnlyList<string>>? selectedNames = _selectedTestName is null ? null :
                    new Dictionary<string, IReadOnlyList<string>> { [packageId] = [_selectedTestName] };
                await RunTestsAsync([packageId], selectedFiles, selectedNames).ConfigureAwait(true);
            }
            else _testState.Text = "Select a discovered test or result first.";
        };
        Button rerun = TestButton("Re-run failed");
        rerun.Click += async (_, _) => await RerunFailedTestsAsync().ConfigureAwait(true);
        Button cancel = TestButton("Cancel");
        cancel.Click += (_, _) => CancelTests("Cancelled.");

        WrapPanel actions = new() { Orientation = Orientation.Horizontal };
        foreach (Button button in new[] { discover, all, package, file, selected, rerun, cancel }) actions.Children.Add(button);
        _testPanel.Children.Add(actions);
        _testPanel.Children.Add(_testState);
        _testPanel.Children.Add(new TextBlock { Text = "Discovered tests", FontWeight = FontWeight.SemiBold });
        _testPanel.Children.Add(_testFiles);
        _testPanel.Children.Add(new TextBlock { Text = "Results", FontWeight = FontWeight.SemiBold });
        _testPanel.Children.Add(_testResults);
        return BottomTab("Tests", _testPanel);
    }

    private static Button TestButton(string label) => new() { Content = label, MinWidth = 90, Margin = new Avalonia.Thickness(2) };

    private async Task DiscoverTestsAsync()
    {
        if (_activePackage is null)
        {
            _testState.Text = "Select a script package first.";
            return;
        }

        string profileId = SelectedProfileId;
        string packageId = _activePackage.Definition.PackageId;
        _testState.Text = "Discovering tests…";
        _selectedTestFile = null;
        _selectedTestPackageId = null;
        _selectedTestName = null;
        _testFiles.Children.Clear();
        try
        {
            IReadOnlyList<ScriptTestCase> tests = await Task.Run(() => _runtime.ScriptTests.DiscoverAsync(profileId, packageId, _cts.Token), _cts.Token).ConfigureAwait(true);
            if (!profileId.Equals(SelectedProfileId, StringComparison.Ordinal) || _activePackage?.Definition.PackageId != packageId) return;
            foreach (ScriptTestCase test in tests)
            {
                Button row = TestButton(test.FilePath);
                row.HorizontalContentAlignment = HorizontalAlignment.Left;
                row.Click += (_, _) =>
                {
                    _selectedTestPackageId = packageId;
                    _selectedTestFile = test.FilePath;
                    _selectedTestName = null;
                    _testState.Text = $"Selected {test.FilePath}";
                };
                _testFiles.Children.Add(row);
            }
            _testState.Text = tests.Count == 0 ? "No tests discovered in this package." : $"Found {tests.Count} test file(s). Select one to run it.";
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception exception) { _testState.Text = "Discovery failed: " + exception.Message; }
    }

    private async Task RunTestsAsync(
        IReadOnlyList<string> packageIds,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? filesByPackage = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? namesByPackage = null)
    {
        CancelTests("Superseded by a new run.");
        string profileId = SelectedProfileId;
        _testRows.Clear();
        _failedTestGroups.Clear();
        _testResults.Children.Clear();
        if (packageIds.Count == 0)
        {
            _testState.Text = "This profile has no script packages.";
            return;
        }
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _testRunCancellation = cancellation;
        long generation = ++_testRunGeneration;
        _failedTestGroups.Clear();
        _testResults.Children.Clear();
        _testState.Text = "Saving dirty scripts and running tests…";
        _bottom.SelectedIndex = _bottom.Items.Count - 1;
        try
        {
            foreach (string packageId in packageIds)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                string source = "tests:" + packageId;
                _diagnostics.ResolveSource(profileId, source);
                RefreshProblems();
                foreach (StudioDocument document in _documents.Documents.Where(document =>
                             document.IsScript && document.IsDirty && document.ProfileId == profileId && document.PackageId == packageId).ToArray())
                    await SaveScriptAsync(document.Key, build: false).ConfigureAwait(true);
                IReadOnlyList<string>? files = filesByPackage?.GetValueOrDefault(packageId);
                IReadOnlyList<string>? names = namesByPackage?.GetValueOrDefault(packageId);
                ScriptTestRunResult result = await _runtime.ScriptTests.RunAsync(
                    new ScriptTestSelection(profileId, packageId, files, names), cancellation.Token).ConfigureAwait(true);
                if (generation != _testRunGeneration || profileId != SelectedProfileId) return;
                foreach (ScriptTestAssertionResult assertion in result.Assertions)
                    _testRows.Add(new TestResultRow(packageId, assertion));
                foreach (ScriptTestDiagnostic diagnostic in result.Diagnostics)
                    if (!result.Assertions.Any(assertion => assertion.Status == "failed" && assertion.FilePath == diagnostic.FilePath && assertion.Message == diagnostic.Message))
                        AddTestDiagnostic(packageId, diagnostic);
                IEnumerable<StudioDiagnostic> issues = result.Diagnostics.Select((diagnostic, index) =>
                    new StudioDiagnostic(profileId, source, $"diagnostic:{index}:{diagnostic.FilePath}:{diagnostic.Line}:{diagnostic.Column}:{diagnostic.Message}",
                        diagnostic.Severity.ToString(), diagnostic.Message, packageId, diagnostic.FilePath, diagnostic.Line, diagnostic.Column))
                    .Concat(result.Assertions.Where(assertion => assertion.Status == "failed").Select((assertion, index) =>
                        new StudioDiagnostic(profileId, source, $"assertion:{index}:{assertion.FilePath}:{assertion.Name}", "Error",
                            assertion.Message ?? $"Test failed: {assertion.Name}", packageId, assertion.FilePath, assertion.Line, assertion.Column)));
                _diagnostics.ReplaceSource(profileId, source, issues);
                foreach (string line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    AddConsole($"Tests {packageId}: {line}");
                RefreshProblems();
                if (!result.Success)
                {
                    string[] failedFiles = result.Assertions.Where(assertion => assertion.Status == "failed")
                        .Select(assertion => assertion.FilePath)
                        .Concat(result.Diagnostics.Select(diagnostic => diagnostic.FilePath))
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    string[] failedNames = result.Assertions.Where(assertion => assertion.Status == "failed")
                        .Select(assertion => assertion.Name)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    _failedTestGroups.Add(new FailedTestGroup(packageId, failedFiles, failedNames));
                }
            }
            foreach (TestResultRow row in _testRows) AddTestAssertion(row);
            _testState.Text = $"{_testRows.Count(row => row.Assertion.Status == "passed")} passed · {_testRows.Count(row => row.Assertion.Status == "failed")} failed · {_testRows.Count(row => row.Assertion.Status is "pending" or "todo" or "skipped")} skipped";
            if (_testRows.Count == 0 && _failedTestGroups.Count == 0 && _testResults.Children.Count == 0)
                _testState.Text = "Tests completed with no reported assertions.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (generation == _testRunGeneration) _testState.Text = "Test run cancelled.";
        }
        catch (Exception exception)
        {
            if (generation == _testRunGeneration) _testState.Text = "Test run failed: " + exception.Message;
        }
        finally
        {
            if (ReferenceEquals(_testRunCancellation, cancellation)) _testRunCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task RerunFailedTestsAsync()
    {
        if (_failedTestGroups.Count == 0)
        {
            _testState.Text = "There are no failed tests to re-run.";
            return;
        }
        FailedTestGroup[] groups = _failedTestGroups.ToArray();
        await RunTestsAsync(groups.Select(group => group.PackageId).ToArray(),
            groups.ToDictionary(group => group.PackageId, group => group.Files, StringComparer.Ordinal),
            groups.ToDictionary(group => group.PackageId, group => group.Names, StringComparer.Ordinal)).ConfigureAwait(true);
    }

    private void AddTestAssertion(TestResultRow row)
    {
        ScriptTestAssertionResult assertion = row.Assertion;
        Button item = TestButton($"{assertion.Status}: {row.PackageId}/{assertion.FilePath} — {assertion.Name}{(assertion.Message is null ? "" : "\n" + assertion.Message)}");
        item.HorizontalContentAlignment = HorizontalAlignment.Left;
        item.Foreground = assertion.Status == "failed" ? UiTheme.Danger : UiTheme.Text;
        item.Click += async (_, _) =>
        {
            _selectedTestPackageId = row.PackageId;
            _selectedTestFile = assertion.FilePath;
            _selectedTestName = assertion.Name;
            _testState.Text = $"Selected {row.PackageId}/{assertion.FilePath}: {assertion.Name}";
            if (assertion.Line is not null && !string.IsNullOrWhiteSpace(assertion.FilePath))
            {
                await OpenSourceAsync(row.PackageId, assertion.FilePath).ConfigureAwait(true);
                await _monaco.RevealLocationAsync(SourceDocumentUri(row.PackageId, assertion.FilePath), assertion.Line, assertion.Column, _cts.Token).ConfigureAwait(true);
            }
        };
        _testResults.Children.Add(item);
    }

    private void AddTestDiagnostic(string packageId, ScriptTestDiagnostic diagnostic)
    {
        Button item = TestButton($"{packageId}: {diagnostic.Message}");
        item.HorizontalContentAlignment = HorizontalAlignment.Left;
        item.Foreground = UiTheme.Danger;
        item.Click += async (_, _) =>
        {
            if (diagnostic.Line is not null && !string.IsNullOrWhiteSpace(diagnostic.FilePath))
            {
                await OpenSourceAsync(packageId, diagnostic.FilePath).ConfigureAwait(true);
                await _monaco.RevealLocationAsync(SourceDocumentUri(packageId, diagnostic.FilePath), diagnostic.Line, diagnostic.Column, _cts.Token).ConfigureAwait(true);
            }
        };
        _testResults.Children.Add(item);
    }

    private void ResetTestsPanel(string message)
    {
        _selectedTestFile = null;
        _selectedTestPackageId = null;
        _selectedTestName = null;
        _testFiles.Children.Clear();
        _testRows.Clear();
        _failedTestGroups.Clear();
        _testResults.Children.Clear();
        _testState.Text = message;
    }

    private void CancelTests(string reason)
    {
        _testRunGeneration++;
        _testRunCancellation?.Cancel();
        if (_testRunCancellation is not null) _testState.Text = reason;
    }
}

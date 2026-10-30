using System.Windows.Controls;

namespace LmChat;

// Sprachauswahl in den Einstellungen. Oberflächentexte aus der XAML wechseln sofort (DynamicResource),
// im Code erzeugte Texte werden hier neu aufgebaut.
public partial class MainWindow
{
    private bool _loadingLanguage;

    private sealed record LanguageItem(string Code, string Label);

    private void ShowLanguageChoice()
    {
        _loadingLanguage = true;
        var saved = _store.GetSetting("language") ?? "";
        var items = new List<LanguageItem> { new("", L.T("Automatisch (Sprache von Windows)")) };
        items.AddRange(L.Languages.Select(l => new LanguageItem(l.Code, l.Name)));
        LanguageCombo.ItemsSource = items;
        LanguageCombo.DisplayMemberPath = nameof(LanguageItem.Label);
        LanguageCombo.SelectedItem = items.FirstOrDefault(i => i.Code == saved) ?? items[0];
        _loadingLanguage = false;
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingLanguage || LanguageCombo.SelectedItem is not LanguageItem item) return;
        _store.SetSetting("language", item.Code);
        L.Load(item.Code);
        Relocalize();
    }

    // Alles neu beschriften, was im Code zusammengesetzt wird
    private void Relocalize()
    {
        ShowLanguageChoice();
        ShowProviderTexts(ShownProvider);
        UpdateModelUi();
        UpdateProviderRows();
        UpdateSetupGuide();
        ApplyModelFilter();
        ModelCombo_SelectionChanged(ModelCombo, null!);
        UpdatePersonaChip();
        System.Windows.Data.CollectionViewSource.GetDefaultView(_chats)?.Refresh();       // Datumsgruppen
        System.Windows.Data.CollectionViewSource.GetDefaultView(_models)?.Refresh();      // Modell-Beschriftungen
        TemplateList.Items.Refresh();
        var region = RegionCombo.SelectedValue;
        RegionCombo.ItemsSource = null;
        RegionCombo.ItemsSource = RegionOption.All;
        _showingProvider = true;
        RegionCombo.SelectedValue = region;
        _showingProvider = false;
        ProviderList.Items.Refresh();
        if (_cts == null) RenderChat();
        UpdateEmptyState();
        if (MainTabs.SelectedIndex == 3) ShowPlugins();
        if (MainTabs.SelectedIndex == 2)
        {
            _ = ShowUsageAsync();
            _ = ShowBudgetAsync();
        }
        foreach (var side in new[] { _left, _right })
            if (side?.Providers.SelectedItem is ProviderInfo info)
                UpdateCompareKeyPanel(side, info);
        UpdateAutomationUi();
        AutomationProviderCombo.Items.Refresh();
        CompareProviderLeft.Items.Refresh();
        CompareProviderRight.Items.Refresh();
        ProviderList.Items.Refresh();
        StatusText.Text = L.T("Sprache geändert.");
    }
}

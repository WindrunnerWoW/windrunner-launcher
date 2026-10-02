using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using WindrunnerLauncher.App.ViewModels;

namespace WindrunnerLauncher.App.Views.Dialogs;

public enum ServerUpdateChoice
{
    Later,
    Skip,
    Update
}

/// <summary>Release notes for the pending server release, with Update now / Skip / Later.</summary>
public sealed partial class ServerUpdateDialog : Window
{
    public ServerUpdateDialog() => AvaloniaXamlLoader.Load(this);

    public ServerUpdateDialog(MainViewModel vm) : this()
    {
        var texts = vm.Texts;
        var latest = vm.LatestServerVersion ?? texts["server.update.unknown"];
        Title = texts.Format("server.update.dialog.title", latest);
        this.FindControl<TextBlock>("HeadingText")!.Text = Title;
        this.FindControl<TextBlock>("VersionsText")!.Text = vm.ServerUpdateVersions;
        this.FindControl<TextBlock>("NotesHeading")!.Text = texts["server.update.dialog.notes"];
        this.FindControl<SelectableTextBlock>("NotesText")!.Text = vm.ServerReleaseNotes;

        var running = this.FindControl<TextBlock>("RunningText")!;
        running.Text = texts["server.update.dialog.running"];
        running.IsVisible = vm.IsServerRunning;
        this.FindControl<TextBlock>("BackupText")!.Text = texts["server.update.dialog.backup"];

        this.FindControl<Button>("SkipButton")!.Content = texts["server.update.skip.version"];
        this.FindControl<Button>("LaterButton")!.Content = texts["server.update.later"];
        this.FindControl<Button>("UpdateButton")!.Content = texts["server.update.now"];
    }

    private void SkipClicked(object? sender, RoutedEventArgs e) => Close(ServerUpdateChoice.Skip);
    private void LaterClicked(object? sender, RoutedEventArgs e) => Close(ServerUpdateChoice.Later);
    private void UpdateClicked(object? sender, RoutedEventArgs e) => Close(ServerUpdateChoice.Update);
}

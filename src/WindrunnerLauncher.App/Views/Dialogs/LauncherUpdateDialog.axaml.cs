using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using WindrunnerLauncher.App.ViewModels;

namespace WindrunnerLauncher.App.Views.Dialogs;

public enum LauncherUpdateChoice
{
    Skip,
    Ignore,
    Update
}

/// <summary>Announced launcher build, with Update now / Skip / Ignore this version.</summary>
public sealed partial class LauncherUpdateDialog : Window
{
    public LauncherUpdateDialog() => AvaloniaXamlLoader.Load(this);

    public LauncherUpdateDialog(MainViewModel vm) : this()
    {
        var texts = vm.Texts;
        var latest = vm.LatestLauncherVersion ?? texts["server.update.unknown"];
        Title = texts.Format("launcher.update.dialog.title", latest);
        this.FindControl<TextBlock>("HeadingText")!.Text = Title;
        this.FindControl<TextBlock>("VersionsText")!.Text = vm.LauncherUpdateVersions;
        this.FindControl<TextBlock>("BodyText")!.Text = texts["launcher.update.dialog.body"];

        var notes = vm.LauncherUpdateNotes;
        var notesPanel = this.FindControl<StackPanel>("NotesPanel")!;
        notesPanel.IsVisible = notes.Length > 0;
        if (notesPanel.IsVisible)
        {
            this.FindControl<TextBlock>("NotesHeading")!.Text = texts["launcher.update.dialog.notes"];
            this.FindControl<SelectableTextBlock>("NotesText")!.Text = notes;
        }

        this.FindControl<Button>("IgnoreButton")!.Content = texts["launcher.update.ignore"];
        this.FindControl<Button>("SkipButton")!.Content = texts["launcher.update.skip"];
        this.FindControl<Button>("UpdateButton")!.Content = texts["launcher.update.now"];
    }

    private void IgnoreClicked(object? sender, RoutedEventArgs e) => Close(LauncherUpdateChoice.Ignore);
    private void SkipClicked(object? sender, RoutedEventArgs e) => Close(LauncherUpdateChoice.Skip);
    private void UpdateClicked(object? sender, RoutedEventArgs e) => Close(LauncherUpdateChoice.Update);
}

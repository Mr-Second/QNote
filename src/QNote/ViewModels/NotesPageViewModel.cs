using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using QNote.Models;
using QNote.Services;

namespace QNote.ViewModels;

/// <summary>
/// The notes screen (three-pane) view-model. Owns the summary list, the current
/// selection, and the editor fields. Editing folds in here for now — a dedicated
/// EditorViewModel arrives with the RTF task. Save model: flush on navigate-away /
/// close / Ctrl+S, surfaced by <see cref="IsDirty"/>.
/// </summary>
public partial class NotesPageViewModel : ObservableObject
{
    private const int PreviewLength = 120;

    private readonly INoteService _notes;
    private readonly ILogger<NotesPageViewModel> _log;

    private Note? _loaded;        // full note currently shown in the editor
    private bool _suppressDirty;  // true while loading editor fields programmatically

    public NotesPageViewModel(INoteService notes, ILogger<NotesPageViewModel> log)
    {
        _notes = notes;
        _log = log;
        Notes.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(IsEmpty));
        };
    }

    /// <summary>Raised when a freshly created note wants keyboard focus in the title box.</summary>
    public event Action? FocusTitleRequested;

    public ObservableCollection<NoteItemViewModel> Notes { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedTimeDisplay))]
    public partial NoteItemViewModel? SelectedNote { get; set; }

    [ObservableProperty]
    public partial string EditingTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditingContent { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsDirty { get; set; }

    public bool HasSelection => SelectedNote is not null;

    /// <summary>The selected note's timestamp for the editor meta line (flat + null-safe).</summary>
    public string SelectedTimeDisplay => SelectedNote?.TimeDisplay ?? string.Empty;

    public bool IsEmpty => Notes.Count == 0;

    public string CountText => Notes.Count > 0 ? $"共 {Notes.Count} 条" : "暂无便签";

    /// <summary>Initial load of the summary list (called from the page's Loaded event).</summary>
    public async Task LoadAsync()
    {
        try
        {
            var summaries = await _notes.GetSummariesAsync();
            Notes.Clear();
            foreach (var summary in summaries)
                Notes.Add(new NoteItemViewModel(summary));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "加载便签列表失败");
        }
    }

    /// <summary>On selection change: flush the previous note, then lazy-load the new one.</summary>
    public async Task OnSelectionChangedAsync()
    {
        try
        {
            await FlushAsync();

            var selected = SelectedNote;
            if (selected is null)
            {
                SetEditor(null, string.Empty, string.Empty);
                return;
            }

            var full = await _notes.GetByIdAsync(selected.Id);
            SetEditor(full, full?.Title ?? string.Empty, full?.Content ?? string.Empty);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "切换便签失败");
        }
    }

    /// <summary>Persist the current editor content if dirty (navigate-away / close / Ctrl+S).</summary>
    public async Task FlushAsync()
    {
        if (_loaded is null || !IsDirty)
            return;

        try
        {
            var saved = await _notes.UpdateAsync(_loaded with { Title = EditingTitle, Content = EditingContent });
            _loaded = saved;
            IsDirty = false;

            var item = Notes.FirstOrDefault(n => n.Id == saved.Id);
            if (item is not null)
            {
                item.Apply(saved.Title, MakePreview(saved.Content), saved.UpdatedAt);
                OnPropertyChanged(nameof(SelectedTimeDisplay));
                var index = Notes.IndexOf(item);
                if (index > 0)
                    Notes.Move(index, 0); // bumped UpdatedAt → newest → top of the list
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "保存便签失败");
        }
    }

    /// <summary>Delete the selected note (the View confirms before calling this).</summary>
    public async Task DeleteSelectedNoteAsync()
    {
        var selected = SelectedNote;
        if (selected is null)
            return;

        try
        {
            _loaded = null;   // don't let the follow-up selection change flush a deleted note
            IsDirty = false;
            await _notes.DeleteAsync(selected.Id);
            Notes.Remove(selected); // removing the selected item clears selection → editor empty state
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "删除便签失败");
        }
    }

    [RelayCommand]
    private async Task NewNoteAsync()
    {
        try
        {
            await FlushAsync();
            var created = await _notes.CreateAsync();
            var item = NoteItemViewModel.FromNote(created);
            Notes.Insert(0, item);
            SelectedNote = item; // → OnSelectionChangedAsync loads the blank note into the editor
            FocusTitleRequested?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "新建便签失败");
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => FlushAsync();

    private bool CanSave() => IsDirty;

    private void SetEditor(Note? loaded, string title, string content)
    {
        _loaded = loaded;
        _suppressDirty = true;
        EditingTitle = title;
        EditingContent = content;
        _suppressDirty = false;
        IsDirty = false;
    }

    partial void OnEditingTitleChanged(string value)
    {
        if (!_suppressDirty)
            IsDirty = true;
    }

    partial void OnEditingContentChanged(string value)
    {
        if (!_suppressDirty)
            IsDirty = true;
    }

    private static string MakePreview(string content)
    {
        var oneLine = content.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= PreviewLength ? oneLine : oneLine[..PreviewLength];
    }
}

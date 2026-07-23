using MediaCatalog.App.Infrastructure;
using MediaCatalog.Core.Naming;

namespace MediaCatalog.App.ViewModels;

/// <summary>A rename proposal shown in the preview dialog, with a tick to include it.</summary>
public class RenameRow : ObservableObject
{
    private bool _isSelected = true;

    public RenameRow(RenameProposal proposal) => Proposal = proposal;

    public RenameProposal Proposal { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string CurrentName => Proposal.CurrentName;
    public string ProposedName => Proposal.ProposedName;
    public string Folder => System.IO.Path.GetDirectoryName(Proposal.File.FullPath) ?? "";
}

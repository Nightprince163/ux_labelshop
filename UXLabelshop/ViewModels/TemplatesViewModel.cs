using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UXLabelshop.Services;

namespace UXLabelshop.ViewModels;

public class TemplateRowVm
{
    public long Id { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string Status { get; init; } = "";
    public string UpdatedText { get; init; } = "";

    public string StatusText => Status switch
    {
        "published" => "已发布",
        "draft" => "草稿",
        "disabled" => "已停用",
        _ => Status,
    };
}

public partial class TemplatesViewModel : ObservableObject
{
    private readonly TemplateService _templates;

    public ObservableCollection<TemplateRowVm> Rows { get; } = new();

    [ObservableProperty] private TemplateRowVm? _selected;
    [ObservableProperty] private string? _statusMessage;

    public TemplatesViewModel(TemplateService templates) => _templates = templates;

    [RelayCommand]
    public void Load()
    {
        var currentId = Selected?.Id;
        Rows.Clear();
        foreach (var t in _templates.GetRows())
        {
            Rows.Add(new TemplateRowVm
            {
                Id = t.Id,
                Code = t.Code,
                Name = t.Name,
                SizeText = $"{t.WidthMm:0.#}×{t.HeightMm:0.#}mm",
                Status = t.Status,
                UpdatedText = t.UpdatedAt.ToString("yyyy-MM-dd HH:mm"),
            });
        }
        Selected = Rows.FirstOrDefault(r => r.Id == currentId) ?? Rows.FirstOrDefault();
        StatusMessage = $"共 {Rows.Count} 个模板";
    }
}

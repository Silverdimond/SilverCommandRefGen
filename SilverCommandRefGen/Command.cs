namespace SilverCommandRefGen;

public class Command
{
    public string Location { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string[]? Aliases { get; set; }
    public List<string> CustomAttributes { get; set; } = new();
}
namespace SilverCommandRefGen;

public class Command
{
    public string Location { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string[]? Aliases { get; set; }
    public List<string> CustomAttributes { get; set; } = new();
    public List<Argument> Arguments { get; set; } = new();
}

public class Argument
{
    public Argument(string name, string type)
    {
        Name = name;
        Type = type;
    }

    public string Name { get; set; }
    public string Type { get; set; }
}
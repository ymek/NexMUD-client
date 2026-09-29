namespace JevMud.Client.Navigation;

internal static class NavigationCommandClassifier
{
    public static bool IsMovementCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        string normalized = command.Trim().ToLowerInvariant();
        return normalized is
            "n" or "north" or "s" or "south" or "e" or "east" or "w" or "west" or
            "ne" or "northeast" or "nw" or "northwest" or "se" or "southeast" or "sw" or "southwest" or
            "u" or "up" or "d" or "down";
    }
}

namespace KamenRaider.Agent.Enrollment;

public sealed class EnrollmentTicketStore(string directory)
{
    private string FilePath=>Path.Combine(directory,"enrollment-ticket.txt");

    public string? ReadIfPresent()
    {
        if(!File.Exists(FilePath))return null;
        var ticket=File.ReadAllText(FilePath);
        if(!System.Text.RegularExpressions.Regex.IsMatch(ticket,@"\A[A-Za-z0-9_-]{43}\z"))
            throw new InvalidDataException("Enrollment ticket file is invalid");
        return ticket;
    }

    public void Clear()
    {
        if(File.Exists(FilePath))File.Delete(FilePath);
    }
}

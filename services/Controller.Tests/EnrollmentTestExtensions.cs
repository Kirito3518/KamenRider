using KamenRaider.Contracts;
using KamenRaider.Controller.Data;

namespace Controller.Tests;

internal static class EnrollmentTestExtensions
{
    public static EnrollmentResult EnrollWithTicket(this ControllerDatabase database, EnrollmentRequest request)
    {
        var ticket = database.CreateEnrollmentTicket();
        return database.Enroll(request, ticket.Ticket);
    }
}

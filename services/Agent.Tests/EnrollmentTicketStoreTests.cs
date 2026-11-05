using KamenRaider.Agent.Enrollment;

namespace Agent.Tests;

public class EnrollmentTicketStoreTests
{
    [Fact] public void TicketIsPrivateToTheEnrollmentFlowAndRemovedAfterAcknowledgement()
    {
        var directory=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try {
            var store=new EnrollmentTicketStore(directory);
            Assert.Null(store.ReadIfPresent());
            var ticket=new string('A',43);File.WriteAllText(Path.Combine(directory,"enrollment-ticket.txt"),ticket);
            Assert.Equal(ticket,store.ReadIfPresent());
            store.Clear();Assert.Null(store.ReadIfPresent());
        } finally {Directory.Delete(directory,true);}
    }

    [Fact] public void MalformedTicketIsRejectedInsteadOfBeingSent()
    {
        var directory=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try {
            File.WriteAllText(Path.Combine(directory,"enrollment-ticket.txt"),"not-a-ticket");
            Assert.Throws<InvalidDataException>(()=>new EnrollmentTicketStore(directory).ReadIfPresent());
        } finally {Directory.Delete(directory,true);}
    }
}

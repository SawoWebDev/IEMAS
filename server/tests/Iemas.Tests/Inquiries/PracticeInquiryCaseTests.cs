using Iemas.Application.Inquiries;
using Iemas.Domain.Email;
using Microsoft.EntityFrameworkCore;
using static Iemas.Tests.Inquiries.InquiryHarness;

namespace Iemas.Tests.Inquiries;

public class PracticeInquiryCaseTests
{
    private const string Agent1 = "agent1@sawo.com";
    private const string System = "icare@sawo.com";

    [Fact]
    public async Task PracticeEmail_FromIemasOwnNotificationMailbox_OpensAnInquiryAndPopsUp()
    {
        using var h = new InquiryHarness(Agent1);
        h.Db.EmailAccounts.Add(new EmailAccount
        {
            EmailAddress = System, Purpose = EmailAccountPurpose.Outbound, Protocol = EmailProtocol.Smtp, Host = "smtp.test", Port = 587,
            Encryption = "STARTTLS", Username = System, AuthMethod = EmailAuthMethod.Password, IsActive = true,
        });
        await h.Db.SaveChangesAsync();
        h.Receive(Agent1, System, $"{PracticeInquiry.SubjectTag(Agent1)} Quotation request: SAWO heater for a home sauna",
            "Good morning, could you please send us a quotation for a suitable heater?", Monday0900, fromName: "IEMAS");

        await h.RunAsync(Monday0900.AddMinutes(1));

        var c = await h.CaseAsync(Agent1, System);
        Assert.Equal(h.Agents[Agent1].Id, c.OwnerEmployeeId);
        Assert.True(PracticeInquiry.IsPractice(c));
        Assert.Contains("New Email", h.PopUpTitlesFor(Agent1));
    }
}

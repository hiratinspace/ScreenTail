using ScreenTail.Core.Intel;

namespace ScreenTail.Tests.Intel;

/// <summary>
/// Reading a ticket number off what is in front of the technician when a session starts (ST-077). The
/// title wins over the clipboard, a number is only a ticket when it looks like one, and what comes out is
/// digits — never the title, never the clipboard (INV-10, AC3).
/// </summary>
public sealed class TicketHintTests
{
    [Theory]
    [InlineData("Ticket #48213 - Printer offline - ConnectWise Manage", "48213")]
    [InlineData("#48213 Printer offline", "48213")]
    [InlineData("Service Ticket 48213 — Acme Dental", "48213")]
    [InlineData("SR 50011: New starter", "50011")]
    [InlineData("Case #7 open", null)]
    [InlineData("Printer offline", null)]
    [InlineData("Build 2026.09.25 - Notepad", null)]
    [InlineData("", null)]
    public void ATitleGivesUpItsTicketNumberOrNothing(string title, string? expected)
    {
        Assert.Equal(expected, TicketHint.From(title: title, browserTabTitle: null, clipboard: null));
    }

    [Fact]
    public void TheBrowserTabIsTheTitleThatMatters()
    {
        // A browser's window title is the tab's plus the browser's name; the tab is what the technician
        // is looking at, and the window title of a PSA in a browser ends with "- Google Chrome".
        Assert.Equal("48213", TicketHint.From("Acme - Google Chrome", browserTabTitle: "#48213 · Printer offline", clipboard: null));
    }

    [Fact]
    public void TheClipboardIsAskedOnlyWhenTheTitleSaysNothing()
    {
        Assert.Equal("48213", TicketHint.From("Remote Desktop", null, clipboard: "48213"));
        Assert.Equal("48213", TicketHint.From("Remote Desktop", null, clipboard: "ticket #48213 please"));
        Assert.Equal("50011", TicketHint.From("Ticket #50011", null, clipboard: "48213"));
    }

    [Fact]
    public void AClipboardFullOfOtherThingsIsNotATicket()
    {
        // A phone number, a card number, an IP: digits that are not a ticket. Only a bare number, or a
        // number introduced as a ticket, counts — and nothing of the clipboard survives either way.
        Assert.Null(TicketHint.From("Remote Desktop", null, clipboard: "call 0161 496 0123"));
        Assert.Null(TicketHint.From("Remote Desktop", null, clipboard: "4111 1111 1111 1111"));
        Assert.Null(TicketHint.From("Remote Desktop", null, clipboard: "192.168.1.10"));
        Assert.Null(TicketHint.From("Remote Desktop", null, clipboard: new string('x', 20_000)));
    }
}

using Xunit.Sdk;
using Xunit.v3;

namespace ScreenTail.Tests.Windows;

/// <summary>
/// The one collection that makes a WPF <see cref="System.Windows.Application"/>, and it runs last.
///
/// WPF allows one Application per process for its whole life. Once it has shut down, no XAML component
/// loads again in that process: <c>InitializeComponent</c> throws "The Application object is being shut
/// down". The pill is a XAML window, so a shell test that ran after this collection could not build it.
/// Left running instead, the Application would hold a dispatcher whose thread had ended, and every model
/// that hands its changes to <c>Application.Current</c>'s dispatcher would queue them for nobody. So the
/// tests that need the app's styles share this collection, and <see cref="ApplicationOwnerRunsLast"/> puts
/// it after every other. The assembly runs one collection at a time (AssemblyInfo.cs), so last is last.
///
/// The Application itself is the collection's fixture (<see cref="Accessibility.TheApplication"/>, ST-084):
/// made once when the collection starts, on a thread that pumps until it ends, and shut down after its
/// last test. Until the accessibility suite there was one test that needed it, and it made and shut down
/// its own; a second test could not have done the same.
/// </summary>
[CollectionDefinition(Name)]
public sealed class WpfApplicationOwner : ICollectionFixture<Accessibility.TheApplication>
{
    public const string Name = "Owns the WPF Application";
}

/// <summary>The default order, with <see cref="WpfApplicationOwner"/>'s collection moved to the end.</summary>
public sealed class ApplicationOwnerRunsLast : ITestCollectionOrderer
{
    public IReadOnlyCollection<TTestCollection> OrderTestCollections<TTestCollection>(IReadOnlyCollection<TTestCollection> testCollections)
        where TTestCollection : notnull, ITestCollection
    {
        var ordered = DefaultTestCollectionOrderer.Instance.OrderTestCollections(testCollections);
        return [.. ordered.Where(c => !OwnsTheApplication(c)), .. ordered.Where(c => OwnsTheApplication(c))];
    }

    private static bool OwnsTheApplication(ITestCollection collection) =>
        collection.TestCollectionDisplayName == WpfApplicationOwner.Name
        || collection.TestCollectionClassName == typeof(WpfApplicationOwner).FullName;
}

using PhotoLibrary.Core;
using Xunit;

namespace PhotoLibrary.Tests;

public class DateRulesTests
{
    [Theory]
    [InlineData("20190106_125200000_iOS.jpg", "2019-01-06 12:52:00")]
    [InlineData("2014-04-24 15.54.51.jpg", "2014-04-24 15:54:51")]
    [InlineData("IMG_20150410_180731.jpg", "2015-04-10 18:07:31")]
    [InlineData("PXL_20240519_143847545.jpg", "2024-05-19 14:38:47")]
    [InlineData("Call-of-Duty__2024-11-17__23-46-25.mp4", "2024-11-17 23:46:25")]
    [InlineData("VID_20110805_192510.m4v", "2011-08-05 19:25:10")]
    [InlineData("Screenshot 2020-08-29 174943.png", "2020-08-29 17:49:43")]
    [InlineData("2013-09-14 19.24.40-2.jpg", "2013-09-14 19:24:40")]
    public void FileNameWithTime(string name, string expected)
    {
        Assert.Equal(DateTime.Parse(expected), DateRules.FromFileName(name));
        Assert.Equal(DateTime.Parse(expected), DateRules.FromFileName(name, needTime: true));
    }

    [Theory]
    [InlineData("2010-01-29_11.JPG", "2010-01-29")]
    [InlineData("2010-01-27_0.JPG", "2010-01-27")]
    public void FileNameDateOnly(string name, string expected)
    {
        Assert.Equal(DateTime.Parse(expected), DateRules.FromFileName(name));
        Assert.Null(DateRules.FromFileName(name, needTime: true));
    }

    [Theory]
    [InlineData("IMAG0103.jpg")]
    [InlineData("DSC_0361.JPG")]
    [InlineData("67687601_10217351084582138_763234337247199232_n.jpg")]
    [InlineData("Earth-Space-4k-uhd-wallpaper.jpg")]
    public void NoDateInName(string name) => Assert.Null(DateRules.FromFileName(name));

    [Fact]
    public void LooseParsesExifAndIso()
    {
        Assert.Equal(new DateTime(2019, 7, 23, 22, 13, 49), DateRules.ParseLoose("2019:07:23 22:13:49"));
        Assert.Equal(new DateTime(2019, 7, 23, 22, 13, 49), DateRules.ParseLoose("2019-07-23T22:13:49+0100"));
        Assert.Null(DateRules.ParseLoose("0000:00:00 00:00:00"));
    }

    [Fact]
    public void DatedFolderLayout() =>
        Assert.Equal(Path.Combine("2008", "2008_08", "2008_08_11"), DateRules.DatedFolder(new DateTime(2008, 8, 11, 9, 0, 0)));
}

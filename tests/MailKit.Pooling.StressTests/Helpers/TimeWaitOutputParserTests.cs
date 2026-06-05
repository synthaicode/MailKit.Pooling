namespace MailKit.Pooling.StressTests.Helpers;

public sealed class TimeWaitOutputParserTests
{
    [Fact]
    public void TryParseWindowsCount_Returns_Count()
    {
        var success = TimeWaitOutputParser.TryParseWindowsCount("3\r\n", out var count);

        Assert.True(success);
        Assert.Equal(3, count);
    }

    [Fact]
    public void CountLinuxSsTimeWait_Counts_Remote_Port_Matches()
    {
        const string output = """
State     Recv-Q Send-Q Local Address:Port  Peer Address:Port
TIME-WAIT 0      0      172.18.0.2:41100   172.18.0.3:25
TIME-WAIT 0      0      172.18.0.2:41102   172.18.0.3:25
TIME-WAIT 0      0      172.18.0.2:41104   172.18.0.3:587
""";

        var count = TimeWaitOutputParser.CountLinuxSsTimeWait(output, 25);

        Assert.Equal(2, count);
    }

    [Fact]
    public void CountLinuxNetstatTimeWait_Counts_Remote_Port_Matches()
    {
        const string output = """
tcp        0      0 172.18.0.2:41100        172.18.0.3:25           TIME_WAIT
tcp        0      0 172.18.0.2:41102        172.18.0.3:25           TIME_WAIT
tcp6       0      0 :::41104                :::587                  TIME_WAIT
""";

        var count = TimeWaitOutputParser.CountLinuxNetstatTimeWait(output, 25);

        Assert.Equal(2, count);
    }

    [Fact]
    public void CountMacOsNetstatTimeWait_Counts_Remote_Port_Matches()
    {
        const string output = """
tcp4       0      0  127.0.0.1.41100       127.0.0.1.25           TIME_WAIT
tcp4       0      0  127.0.0.1.41102       127.0.0.1.25           TIME_WAIT
tcp4       0      0  127.0.0.1.41104       127.0.0.1.587          TIME_WAIT
""";

        var count = TimeWaitOutputParser.CountMacOsNetstatTimeWait(output, 25);

        Assert.Equal(2, count);
    }
}

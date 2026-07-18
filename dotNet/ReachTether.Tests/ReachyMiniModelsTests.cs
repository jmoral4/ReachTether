using ReachyMini.Sdk.Models;
using Xunit;

namespace ReachTether.Tests;

public class ReachyMiniModelsTests
{
    [Fact]
    public void XYZRPYPose_ShouldInitializeCorrectly()
    {
        // Arrange & Act
        var pose = new XYZRPYPose
        {
            X = 1.0,
            Y = 2.0,
            Z = 3.0,
            Roll = 0.1,
            Pitch = 0.2,
            Yaw = 0.3
        };

        // Assert
        Assert.Equal(1.0, pose.X);
        Assert.Equal(2.0, pose.Y);
        Assert.Equal(3.0, pose.Z);
        Assert.Equal(0.1, pose.Roll);
        Assert.Equal(0.2, pose.Pitch);
        Assert.Equal(0.3, pose.Yaw);
    }

    [Fact]
    public void AppInfo_ShouldStoreValues()
    {
        // Arrange & Act
        var info = new AppInfo
        {
            Name = "TestApp",
            Description = "A test application",
            SourceKind = SourceKind.Local,
            Url = "http://localhost"
        };

        // Assert
        Assert.Equal("TestApp", info.Name);
        Assert.Equal("A test application", info.Description);
        Assert.Equal(SourceKind.Local, info.SourceKind);
        Assert.Equal("http://localhost", info.Url);
    }
}

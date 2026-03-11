# PUBLISH, DEPLOYING, AND TESTING ON REACHY MINI ROBOT 

## WINDOWS
### PUBLISH 
Publish as Linux-arm64 using Visual Studio.
dotnet publish dotNet/ReachTether.Robot/ReachTether.Robot.csproj `
     -c Release -r linux-arm64 --self-contained false `
     -o C:/git/reachy-apps/reachtether/out/reachrobot

### DEPLOY 
Deploy Published artifacts to Reachy using scp - into pre-existing reachrobot directory
scp -r scp -r "C:/git/reachy-apps/reachtether/out/reachrobot/." `
     "pollen@reachy-mini.local:/home/pollen/reachrobot/"

> password: root   (default)

### TESTING 
1. REMOTE INTO ROBOT:
	> ssh pollen@reachy-mini.local 
	> password: root  (default)

2. Navigate to /home/pollen/reachrobot/




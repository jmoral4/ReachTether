# PUBLISH, DEPLOYING, AND TESTING ON REACHY MINI ROBOT 

## WINDOWS
### PUBLISH 
Publish as Linux-arm64 using Visual Studio.

### DEPLOY 
Deploy Published artifacts to Reachy using scp - into pre-existing reachdotnet directory
scp -r "C:/git/reachy-apps/reachtether/dotNet/ReachTether.Robot/bin/Release/net9.0/publish/linux-arm64/." "pollen@reachy-mini.local:/home/pollen/reachrobot/"

> password: root   (default)

### TESTING 
1. REMOTE INTO ROBOT:
	> ssh pollen@reachy-mini.local 
	> password: root  (default)

2. Navigate to /home/pollen/reachdotnet/

3. run: dotnet ChattyReachyMini.dll

### IMPORTANT AGENT NOTE
Ensure you've setup a flag in the app so that it can launch, run your test, and close. Otherwise you will start an interactive process and will block resources on the robot.

## LINUX/WSL
### PUBLISH
Publish from terminal as Linux-arm64:
> cd /mnt/c/git/reachy-apps/reachtether/dotNet/samples/ChattyReachyMini
> dotnet publish -c Release -r linux-arm64 --self-contained false

### DEPLOY
Deploy published artifacts to Reachy using `scp` into the pre-existing `reachdotnet` directory:
> scp -r /mnt/c/git/reachy-apps/reachtether/dotNet/samples/ChattyReachyMini/bin/Release/net9.0/linux-arm64/publish/. pollen@reachy-mini.local:/home/pollen/reachdotnet/
> password: root

### TESTING
1. Remote into robot:
	> ssh pollen@reachy-mini.local
	> password: root

2. Navigate to `/home/pollen/reachdotnet/`

3. Run:
	> dotnet ChattyReachyMini.dll

### IMPORTANT AGENT NOTE
Ensure you've setup a flag in the app so that it can launch, run your test, and close. Otherwise you will start an interactive process and will block resources on the robot.

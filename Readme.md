# pyApp
The main Reachy Mini app that is deployed on the Robot. 

It will mostly be a control stub that forwards data to the dotnet client and executes commands on-behalf of the server that might be latency sensitive. 

# dotNet 
The main app we're working on. 

 scp -r "C:/git/reachy-apps/reachtether/dotNet/samples/ChattyReachyMini/bin/Release/net9.0/publish/linux-arm64/." "pollen@reachy-mini.local:/home/pollen/reachdotnet/"
 
C:\git\reachy-apps\reachtether\dotNet\samples\ChattyReachyMini\bin\Release\net9.0\publish\linux-arm64
C:\git\reachy-apps\reachy_mini-csharp-sdk\samples\ChattyReachyMini\bin\Release\net9.0\publish

C:\git\reachy-apps\reachtether\dotNet\samples\ChattyReachyMini\bin\Release\net9.0\publish\linux-arm64
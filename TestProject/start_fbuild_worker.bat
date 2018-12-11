echo off & color 0A
set FASTBUILD_BROKERAGE_PATH=\\ZT-2017245\FASTBuildShared
tasklist | find /i "FBuildWorker.exe.copy" || start /min "" FBuildWorker.exe -cpus=3 -mode=idle
set bag=getobject("winmgmts:\\.\root\cimv2")
set pipe=bag.execquery("select * from win32_process where name='FBuildWorker.exe'")
For Each id In pipe
    wscript.quit
Next
wscript.CreateObject("Wscript.Shell").Environment("user").Item("FASTBUILD_BROKERAGE_PATH")="\\ZT-2017245\FASTBuildShared" 
wscript.CreateObject("Wscript.Shell").Run "FBuildWorker.exe -cpus=2 -mode=idle -console",0
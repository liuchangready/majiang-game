@echo off
set "JAVA_HOME=C:\Program Files\Android\Android Studio\jbr"
cd /d "D:\majiang"
call gradlew.bat :lwjgl3:run
if errorlevel 1 pause

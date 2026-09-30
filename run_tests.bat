@echo off
set "JAVA_HOME=C:\Program Files\Android\Android Studio\jbr"
cd /d "D:\majiang"
call gradlew.bat test --info
pause

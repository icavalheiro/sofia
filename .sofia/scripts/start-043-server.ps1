# Spec 043: throwaway server for the mutating verification. The data dir is set here, in a real script file, because
# nested `powershell -Command "$env:SOFIA_HOME=..."` quoting loses the assignment and writes to ~/.sofia instead.
$env:SOFIA_HOME = 'C:\Users\ivans\AppData\Local\Temp\sofia-043'
Set-Location 'D:\repos\opensource\sofia'
Write-Output "SOFIA_HOME=$env:SOFIA_HOME"
dotnet run --project src/Sofia.Server --urls http://127.0.0.1:5299

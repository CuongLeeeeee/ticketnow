$global:gw = "http://localhost:5000"

function Call($Method, $Path, $Body, $Headers = @{}) {
    $p = @{
        Method = $Method; Uri = "$global:gw$Path"; Headers = $Headers; ContentType = "application/json"
        SkipHttpErrorCheck = $true; StatusCodeVariable = "sc"; ResponseHeadersVariable = "rh"
    }
    if ($null -ne $Body) { $p.Body = $Body | ConvertTo-Json -Depth 5 }
    $res = Invoke-RestMethod @p
    [pscustomobject]@{ Status = $sc; Body = $res; Replayed = $rh["Idempotent-Replayed"] }
}

function WaitFor([scriptblock]$Get, $Expected, [int]$TimeoutSec = 15) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do {
        $v = & $Get
        if ($v -eq $Expected) { return "OK: $v (after $([int]$sw.Elapsed.TotalMilliseconds) ms)" }
        Start-Sleep -Milliseconds 300
    } while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec)
    "TIMEOUT - last value: $v"
}

function Login($Email, $Password) {
    $r = Call Post "/api/auth/login" @{ email = $Email; password = $Password }
    if ($r.Status -ne 200) { throw "Login failed for ${Email}: $($r.Status)" }
    $r.Body
}

Write-Host "Loaded: Call, WaitFor, Login" -ForegroundColor Green
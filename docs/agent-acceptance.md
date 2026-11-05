# Agent acceptance record

Status: **NOT READY for rentals**. The software lifecycle test uses simulated Agent messages, command results and boot proof. It does not exercise a production Agent, Sunshine, Restore Rx, a real stream fence, or a physical machine. Production reconciliation now wires lease import into `LeaseEnforcer`, but restoration evidence, an enforcing gate and desktop health checks remain unavailable; see [agent operations](agent-operations.md) and [Sunshine compatibility](sunshine-compatibility.md).

| Hardware scenario | Result | Evidence required before acceptance |
| --- | --- | --- |
| Wake-on-LAN from shutdown | NOT TESTED | Named test client, network and cold shutdown; record wake and readiness |
| Expiry while offline | NOT TESTED | Disconnect Agent through expiry; verify access terminates locally |
| Restart Controller, web and Agent | NOT TESTED | Restart each during a lease; verify expiry and access |
| Cold boot desktop | NOT TESTED | Verify desktop, capture, input and audio before readiness |
| Restore Rx | NOT TESTED | Verify restoration on every boot and identity survives |
| Old Moonlight client denied | NOT TESTED | Pair, expire, restore and attempt reconnect, including an active stream |
| Two requests collide | NOT TESTED | Concurrent requests against the deployed system; verify one allocation |
| Custom internet ports | NOT TESTED | Connect from a real external network using configured ports |

No designated and available test client was supplied. Record machine, installed versions, date, operator, method and evidence for each future pass or failure. Fake tests must never be entered as hardware passes.

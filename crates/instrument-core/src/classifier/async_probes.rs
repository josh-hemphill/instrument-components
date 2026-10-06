use crate::scpi::AsyncScpiSession;
use std::time::Duration;

/// Returns true when any probe command succeeds on the async session.
pub async fn probe_any_async(
    session: &mut AsyncScpiSession,
    commands: &[&str],
    timeout: Duration,
) -> bool {
    for cmd in commands {
        if session
            .query_with_timeout(cmd, timeout)
            .await
            .is_ok_and(|reply| super::probes::valid_probe_reply(cmd, &reply))
        {
            return commands != super::probes::PSU_READONLY_COMMANDS
                || session
                    .query_with_timeout(":VOLT? (@1)", timeout)
                    .await
                    .is_ok_and(|reply| super::probes::valid_probe_reply(":VOLT? (@1)", &reply));
        }
    }
    false
}

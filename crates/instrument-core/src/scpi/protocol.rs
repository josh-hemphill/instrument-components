use crate::error::{Error, Result};

/// Cached instrument capabilities discovered at runtime.
#[derive(Debug, Default, Clone, Copy)]
pub(crate) struct SessionCapabilities {
    pub syst_err: Option<bool>,
    pub opc: Option<bool>,
}

/// Appends the terminator when missing from a command string.
pub(crate) fn normalize_command(command: &str, terminator: &str) -> String {
    let mut payload = command.to_string();
    if !payload.ends_with(terminator) {
        payload.push_str(terminator);
    }
    payload
}

/// Returns the maximum write attempts for a command.
pub(crate) fn max_write_attempts(idempotent: bool, retries: u32) -> u32 {
    if idempotent {
        retries + 1
    } else {
        1
    }
}

/// Returns true when `*OPC?` produced a real operation-complete reply.
pub fn is_opc_supported_reply(response: &str) -> bool {
    matches!(response.trim(), "1" | "+1")
}

/// Returns true when `SYST:ERR?` produced SCPI error-queue form `code,message`.
pub fn is_syst_err_supported_reply(response: &str) -> bool {
    let s = response.trim().as_bytes();
    let mut i = 0;
    if i < s.len() && (s[i] == b'+' || s[i] == b'-') {
        i += 1;
    }
    let digits_start = i;
    while i < s.len() && s[i].is_ascii_digit() {
        i += 1;
    }
    if i == digits_start {
        return false;
    }
    while i < s.len() && s[i].is_ascii_whitespace() {
        i += 1;
    }
    i < s.len() && s[i] == b','
}

pub(crate) fn is_no_error_reply(response: &str) -> bool {
    response
        .split(',')
        .next()
        .and_then(|s| s.trim().parse::<i32>().ok())
        == Some(0)
}

/// Parses a numeric SCPI response.
pub fn parse_f64(response: &str) -> Result<f64> {
    response
        .trim()
        .parse()
        .map_err(|_| Error::Parse(format!("expected number, got '{response}'")))
}

/// Parses comma-separated numeric SCPI responses (e.g. waveform ASCII data).
pub fn parse_f64_csv(response: &str) -> Result<Vec<f64>> {
    let mut values = Vec::new();
    for part in response.split(',') {
        let trimmed = part.trim();
        if trimmed.is_empty() {
            continue;
        }
        values.push(
            trimmed
                .parse()
                .map_err(|_| Error::Parse(format!("expected number, got '{trimmed}'")))?,
        );
    }
    Ok(values)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_f64_csv_handles_spaces_and_trailing_comma() {
        let values = parse_f64_csv(" 1.0, 2.5 ,3.0,").unwrap();
        assert_eq!(values, vec![1.0, 2.5, 3.0]);
    }

    #[test]
    fn opc_reply_accepts_one_only() {
        assert!(is_opc_supported_reply("1"));
        assert!(is_opc_supported_reply("  +1 \n"));
        assert!(!is_opc_supported_reply("-113,\"Undefined header\""));
        assert!(!is_opc_supported_reply("OK"));
        assert!(!is_opc_supported_reply(""));
    }

    #[test]
    fn syst_err_reply_requires_code_comma() {
        assert!(is_syst_err_supported_reply("0,\"No error\""));
        assert!(is_syst_err_supported_reply("+0, \"No error\""));
        assert!(is_syst_err_supported_reply("-113,\"Undefined header\""));
        assert!(!is_syst_err_supported_reply("OK"));
        assert!(!is_syst_err_supported_reply("1"));
        assert!(!is_syst_err_supported_reply(""));
    }
}

use super::dc_psu::parse_on_off;
use super::dialect_io;
use instrument_core::error::Result;
use instrument_core::kind::InstrumentKind;
use instrument_core::scpi::AsyncScpiSession;
use instrument_core::scpi_commands;
use instrument_core::AsyncInstrumentSession;

/// Async DC power supply session view (IVI-inspired / SCPI :SOURce, :OUTPut).
pub struct AsyncDcPowerSupply {
    session: AsyncInstrumentSession,
}

impl AsyncDcPowerSupply {
    pub fn new(session: AsyncInstrumentSession) -> Self {
        Self { session }
    }

    pub fn session(&self) -> &AsyncInstrumentSession {
        &self.session
    }

    pub fn session_mut(&mut self) -> &mut AsyncInstrumentSession {
        &mut self.session
    }

    fn dialect(&self) -> &'static instrument_core::DialectProfile {
        self.session.dialect_for(InstrumentKind::DcPowerSupply)
    }

    fn cmd(&self, key: &str, vars: &[(&str, String)], fallback: String) -> String {
        dialect_io::try_formatted(self.dialect(), key, vars, fallback)
    }

    /// Returns channel count from the resolved dialect profile (default 1).
    pub fn channel_count(&self) -> u32 {
        self.dialect().channels.max(1)
    }

    /// Sets output voltage in volts (SI) on the given channel (1-based).
    pub async fn set_voltage(&mut self, channel: u32, volts: f64) -> Result<()> {
        let cmd = self.cmd(
            "set_voltage",
            &[
                ("channel", channel.to_string()),
                ("volts", dialect_io::f64_text(volts)),
            ],
            scpi_commands::psu_set_voltage(channel, volts),
        );
        self.session.scpi_mut().write(&cmd).await
    }

    /// Sets current limit in amps (SI) on the given channel.
    pub async fn set_current_limit(&mut self, channel: u32, amps: f64) -> Result<()> {
        let cmd = self.cmd(
            "set_current_limit",
            &[
                ("channel", channel.to_string()),
                ("amps", dialect_io::f64_text(amps)),
            ],
            scpi_commands::psu_set_current_limit(channel, amps),
        );
        self.session.scpi_mut().write(&cmd).await
    }

    /// Enables or disables output on the given channel.
    pub async fn output_enable(&mut self, channel: u32, enabled: bool) -> Result<()> {
        let state = if enabled { "ON" } else { "OFF" };
        let cmd = self.cmd(
            "output_enable",
            &[("channel", channel.to_string()), ("state", state.into())],
            scpi_commands::psu_output_enable(channel, state),
        );
        self.session.scpi_mut().write(&cmd).await
    }

    /// Queries whether output is enabled on the given channel.
    pub async fn output_state_query(&mut self, channel: u32) -> Result<bool> {
        let cmd = self.cmd(
            "output_state_query",
            &[("channel", channel.to_string())],
            scpi_commands::psu_output_state_query(channel),
        );
        let resp = self.session.scpi_mut().query(&cmd).await?;
        parse_on_off(&resp)
    }

    /// Sets over-voltage protection level in volts (SI).
    pub async fn ovp_level(&mut self, channel: u32, volts: f64) -> Result<()> {
        let cmd = self.cmd(
            "ovp_level",
            &[
                ("channel", channel.to_string()),
                ("volts", dialect_io::f64_text(volts)),
            ],
            scpi_commands::psu_ovp_level(channel, volts),
        );
        self.session.scpi_mut().write(&cmd).await
    }

    /// Enables or disables over-voltage protection.
    pub async fn ovp_enable(&mut self, channel: u32, enabled: bool) -> Result<()> {
        let state = if enabled { "ON" } else { "OFF" };
        let cmd = self.cmd(
            "ovp_enable",
            &[("channel", channel.to_string()), ("state", state.into())],
            scpi_commands::psu_ovp_enable(channel, state),
        );
        self.session.scpi_mut().write(&cmd).await
    }

    /// Queries whether over-voltage protection is enabled.
    pub async fn ovp_query(&mut self, channel: u32) -> Result<bool> {
        let cmd = self.cmd(
            "ovp_query",
            &[("channel", channel.to_string())],
            scpi_commands::psu_ovp_query(channel),
        );
        let resp = self.session.scpi_mut().query(&cmd).await?;
        parse_on_off(&resp)
    }

    /// Enables or disables remote sense on the given channel.
    pub async fn sense_enable(&mut self, channel: u32, enabled: bool) -> Result<()> {
        if self.dialect().id == "keysight_n6705c" {
            return Err(instrument_core::error::Error::Unsupported(
                "N6705 remote sense requires INT/EXT; ON/OFF is not supported",
            ));
        }
        let state = if enabled { "ON" } else { "OFF" };
        let cmd = self.cmd(
            "sense_enable",
            &[("channel", channel.to_string()), ("state", state.into())],
            scpi_commands::psu_sense_enable(channel, state),
        );
        self.session.scpi_mut().write(&cmd).await
    }

    /// Reads measured output voltage in volts (SI).
    pub async fn read_voltage(&mut self, channel: u32) -> Result<f64> {
        let cmd = self.cmd(
            "read_voltage",
            &[("channel", channel.to_string())],
            scpi_commands::psu_read_voltage(channel),
        );
        let resp = self.session.scpi_mut().query(&cmd).await?;
        AsyncScpiSession::parse_f64(&resp)
    }

    /// Reads measured output current in amps (SI).
    pub async fn read_current(&mut self, channel: u32) -> Result<f64> {
        let cmd = self.cmd(
            "read_current",
            &[("channel", channel.to_string())],
            scpi_commands::psu_read_current(channel),
        );
        let resp = self.session.scpi_mut().query(&cmd).await?;
        AsyncScpiSession::parse_f64(&resp)
    }
}

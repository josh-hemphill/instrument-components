//! Development-only strict scripts. Production SDK packages never depend on TestKit.
use instrument_api::*;
use instrument_scpi::*;
use std::{
    collections::VecDeque,
    sync::{Arc, Mutex},
    thread::ThreadId,
};
#[derive(Debug, Clone)]
pub enum ScriptStep {
    Write(String),
    WriteFailure(String, DriverError),
    Query(String, Result<String>),
    Recover(Result<()>),
    Close(Result<()>),
}
#[derive(Debug, Default)]
struct State {
    remaining: VecDeque<ScriptStep>,
    events: Vec<String>,
    threads: Vec<ThreadId>,
    opens: usize,
    closes: usize,
    violations: usize,
}
#[derive(Debug, Clone)]
pub struct Script {
    state: Arc<Mutex<State>>,
}
impl Script {
    pub fn new(steps: impl IntoIterator<Item = ScriptStep>) -> Self {
        Self {
            state: Arc::new(Mutex::new(State {
                remaining: steps.into_iter().collect(),
                ..State::default()
            })),
        }
    }
    pub fn events(&self) -> Vec<String> {
        self.state.lock().unwrap().events.clone()
    }
    pub fn opens(&self) -> usize {
        self.state.lock().unwrap().opens
    }
    pub fn closes(&self) -> usize {
        self.state.lock().unwrap().closes
    }
    pub fn assert_complete(&self) -> Result<()> {
        let complete = {
            let state = self.state.lock().unwrap();
            state.remaining.is_empty() && state.violations == 0
        };
        if complete {
            Ok(())
        } else {
            Err(DriverError::new(
                ErrorKind::DriverContractViolation,
                "unconsumed script steps",
            ))
        }
    }
    pub fn assert_thread_affinity(&self) -> Result<()> {
        let state = self.state.lock().unwrap();
        if state
            .threads
            .iter()
            .all(|t| Some(t) == state.threads.first())
            && !state.threads.is_empty()
        {
            Ok(())
        } else {
            Err(DriverError::new(
                ErrorKind::DriverContractViolation,
                "SDK thread affinity violated",
            ))
        }
    }
    fn record_open(&self) {
        let mut state = self.state.lock().unwrap();
        state.opens += 1;
        state.threads.push(std::thread::current().id());
    }
    fn call(&self, kind: &str, message: &str) -> Result<String> {
        let mut state = self.state.lock().unwrap();
        state.threads.push(std::thread::current().id());
        state.events.push(format!("{kind}:{message}"));
        if kind == "close" {
            state.closes += 1;
        }
        let next = state.remaining.pop_front();
        match (kind, next) {
            ("write", Some(ScriptStep::Write(expected))) if message == expected => {
                Ok(String::new())
            }
            ("write", Some(ScriptStep::WriteFailure(expected, failure))) if message == expected => {
                Err(failure)
            }
            ("query", Some(ScriptStep::Query(expected, reply))) if message == expected => reply,
            ("recover", Some(ScriptStep::Recover(result)))
            | ("close", Some(ScriptStep::Close(result))) => result.map(|_| String::new()),
            _ => {
                state.violations += 1;
                Err(DriverError::new(
                    ErrorKind::DriverContractViolation,
                    format!("unexpected {kind}: {message}"),
                ))
            }
        }
    }
}
struct ScriptIo(Script);
impl MessageIo for ScriptIo {
    fn write(&mut self, message: &str) -> Result<()> {
        self.0.call("write", message).map(|_| ())
    }
    fn query(&mut self, message: &str) -> Result<String> {
        self.0.call("query", message)
    }
    fn recover(&mut self) -> Result<()> {
        self.0.call("recover", "").map(|_| ())
    }
    fn close(&mut self) -> Result<()> {
        self.0.call("close", "").map(|_| ())
    }
}
impl Connector for Script {
    fn open(&self, _context: &OpenContext<'_>) -> Result<Box<dyn MessageIo>> {
        self.record_open();
        Ok(Box::new(ScriptIo(self.clone())))
    }
}
#[cfg(feature = "async")]
async fn yield_once() {
    let mut yielded = false;
    std::future::poll_fn(move |cx| {
        if yielded {
            std::task::Poll::Ready(())
        } else {
            yielded = true;
            cx.waker().wake_by_ref();
            std::task::Poll::Pending
        }
    })
    .await
}
#[cfg(feature = "async")]
impl AsyncMessageIo for ScriptIo {
    fn write<'a>(&'a mut self, message: &'a str) -> DriverFuture<'a, ()> {
        Box::pin(async move {
            let result = self.0.call("write", message).map(|_| ());
            yield_once().await;
            result
        })
    }
    fn query<'a>(&'a mut self, message: &'a str) -> DriverFuture<'a, String> {
        Box::pin(async move {
            let result = self.0.call("query", message);
            yield_once().await;
            result
        })
    }
    fn recover(&mut self) -> DriverFuture<'_, ()> {
        Box::pin(async move {
            let result = self.0.call("recover", "").map(|_| ());
            yield_once().await;
            result
        })
    }
    fn close(&mut self) -> Result<()> {
        self.0.call("close", "").map(|_| ())
    }
}
#[cfg(feature = "async")]
impl AsyncConnector for Script {
    fn open<'a>(
        &'a self,
        _context: &'a OpenContext<'_>,
    ) -> DriverFuture<'a, Box<dyn AsyncMessageIo>> {
        Box::pin(async move {
            self.record_open();
            Ok(Box::new(ScriptIo(self.clone())) as Box<dyn AsyncMessageIo>)
        })
    }
}

#[derive(Debug, Clone)]
pub struct CatalogCase {
    pub id: &'static str,
    pub calls: Vec<instrument_api::catalog::CatalogRequest>,
    pub script: Vec<ScriptStep>,
    pub expected: Option<instrument_api::catalog::CatalogReply>,
    pub expected_error: Option<ErrorKind>,
}

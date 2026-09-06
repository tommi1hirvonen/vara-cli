## ADDED Requirements

### Requirement: Progress delivery completes before display teardown
The system SHALL ensure that every progress report emitted during a backup run has either been
rendered or can no longer be rendered before that run's live progress display is torn down, so
that no in-flight, asynchronously-delivered progress report can be rendered against an already
disposed display context.

#### Scenario: Final progress report delivered asynchronously
- **WHEN** a backup run completes and its final progress report is delivered via an asynchronous
  mechanism that does not guarantee delivery before the reporting call returns
- **THEN** the system does not tear down the live progress display until that final report has
  either been rendered or is guaranteed not to render afterward

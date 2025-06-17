# netRSS Changelog

## Version 1.3.0 - 2025-06-17

### Added
- Comprehensive FeedBurner URL support and detection
- Automatic FeedBurner URL resolution to actual feed URLs
- Robust XML content cleaning to handle malformed DOCTYPE declarations
- Enhanced feed validation with FeedBurner-specific handling

### Fixed
- FeedBurner feeds now work properly in both manual validation and background refresh
- Resolved "doctype is an unexpected token" errors from malformed XML
- Fixed hanging during feed save operations
- Eliminated double validation calls during feed save process

### Improved
- Background service now properly handles FeedBurner redirects
- More tolerant XML parsing for various feed providers
- Better error handling and logging for feed processing issues
- Enhanced compatibility with legacy feed formats

## Version 1.2.0 - 2025-05-10

### Added
- New settings for items per page and auto-hide read items
- Improved feed content display with configurable pagination
- Auto-hide functionality for read items with configurable delay

### Changed
- Updated database schema to support new settings
- Enhanced settings UI with new options for feed display preferences

### Improved
- Better handling of null references in OPML import
- More robust feed content display with user-configurable options

## Version 1.1.0 - 2025-05-07

### Added
- New tabbed interface on Home page with Change Log and Highlights sections
- Dedicated Status page for feed monitoring and management
- Dynamic changelog viewer that renders Markdown content

### Changed
- Moved feed status dashboard from Home page to a dedicated Status page
- Updated navigation menu with new Status page link
- Replaced polling mechanism for sidebar updates instead of SignalR

### Improved
- Home page now provides more useful information about the application
- Better organization of application features and monitoring tools

## Version 1.0.0 - 2025-05-01

### Added
- Initial release with core RSS reader functionality
- Feed management with categories
- Feed health monitoring dashboard
- Content filtering capabilities
- Responsive UI design

### Improved
- Automatic background refresh of feeds
- Real-time feed status updates in sidebar

### Fixed
- Issue with sidebar not updating when new items are added in the background

## Version 0.9.0 - 2025-04-20

### Added
- Docker containerization support
- SQLite database integration
- Dapper ORM implementation

### Improved
- Error handling for feed processing
- UI responsiveness and accessibility

## Version 0.8.0 - 2025-04-01

### Added
- Initial project structure
- Basic feed parsing capabilities
- Simple UI for viewing content

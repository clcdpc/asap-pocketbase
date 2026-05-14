# ASAP Technology Stack

## Backend
- **Core**: PocketBase (Go)
- **Database**: SQLite
- **Runtime**: PocketBase JS VM (Goja) for hooks
- **API**: REST via PocketBase built-in and custom routes

## Frontend
- **Framework**: Vanilla JavaScript (ES6+)
- **Styling**: Vanilla CSS3
- **Grid**: [Grid.js](https://gridjs.io/)
- **Icons**: Font Awesome 4.7
- **UI Components**: Bootstrap 4.6 (CSS only)

## Integrations
- **ILS**: Polaris REST API (PAPI)
- **Email**: SMTP
- **Auth**: Polaris PAPI Authenticator (Staff & Patron)

## Development & Deployment
- **Zero Build**: No build steps required. Files served directly from `pb_public/`.
- **Version Control**: Git
- **Documentation**: Markdown

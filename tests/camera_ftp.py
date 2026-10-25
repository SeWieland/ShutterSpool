"""Connect synthetic probes with verified explicit FTPS unless LAN plaintext is selected."""
import ftplib
import ssl


def connection_profile(profile):
    mode = profile.get('ftp', {}).get('mode', 'ExplicitFtps')
    if mode not in ('ExplicitFtps', 'PlainFtpLan', 'LegacyFtps'):
        raise ValueError('Select ExplicitFtps, LegacyFtps or explicit PlainFtpLan')
    return 'ModernFtps' if mode == 'ExplicitFtps' else mode


def authentication_path(profile):
    return {'ModernFtps': '/auth', 'LegacyFtps': '/legacy-auth', 'PlainFtpLan': '/plain-auth'}[connection_profile(profile)]


def connect_camera_ftp(profile, source, timeout=90, port=None):
    settings = profile.get('ftp', {})
    mode = connection_profile(profile)
    hostname = settings.get('hostname', 'ingest.shutterspool.com')
    if mode != 'PlainFtpLan':
        context = ssl.create_default_context(cafile=settings.get('caFile'))
        context.minimum_version = ssl.TLSVersion.TLSv1_2
        if mode == 'LegacyFtps':
            context.maximum_version = ssl.TLSVersion.TLSv1_2
            context.set_ciphers('AES128-SHA:AES256-SHA')
        client = ftplib.FTP_TLS(context=context)
    else:
        client = ftplib.FTP()
    try:
        client.connect(settings.get('address', hostname), port or settings.get('port', 2121), timeout=timeout)
        client.host = hostname
        client.login(source['username'], source['secret'])
        if mode != 'PlainFtpLan':
            client.prot_p()
        return client
    except BaseException:
        client.close()
        raise


def finish_camera_data(stream):
    try:
        if isinstance(stream, ssl.SSLSocket):
            stream.unwrap()
    finally:
        stream.close()

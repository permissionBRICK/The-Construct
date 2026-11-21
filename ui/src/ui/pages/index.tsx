import { useApp } from '../ctx';
import { SignInCards, VaultCards } from '../blocks';
import { lostSignIns } from '../../model/signin';
import { Backups, Connections, Overview, Project, ProjectFile, Projects, Settings } from './vm';
import { Access, Devices, History, Pair, Revoke, Secrets } from './vault';
import { HostPage, Hosts } from './hosts';
import { Construct, CreateVm, FirstRun, Upgrade } from './app';

/** The page for the current route. VM pages need a VM; without one, the create screen stands in. */
export function PageView() {
  const { s, route, vm } = useApp();
  if (s.firstRun) return <FirstRun />;
  if (s.upgrade) return <Upgrade />;
  const p = route.page;
  // What asks for the user (a vault request, a lost sign-in) shows on every page; the overview has its own.
  const asks = p !== 'home' && (s.vaultRequests.length > 0 || lostSignIns(s.vms).length > 0) ? <div class="pg asks"><VaultCards /><SignInCards /></div> : null;
  const needVm = ['home', 'projects', 'project', 'file', 'connections', 'backups', 'settings'].includes(p);
  if (needVm && !vm) return <CreateVm />;
  const body = (() => {
    switch (p) {
      case 'home': return <Overview vm={vm!} />;
      case 'projects': return <Projects vm={vm!} />;
      case 'project': return <Project vm={vm!} name={route.arg} />;
      case 'file': return <ProjectFile vm={vm!} name={route.arg} />;
      case 'connections': return <Connections vm={vm!} />;
      case 'backups': return <Backups vm={vm!} />;
      case 'settings': return <Settings key={vm!.id} vm={vm!} />;
      case 'vault': return <Secrets />;
      case 'access': return <Access />;
      case 'devices': return <Devices />;
      case 'pair': return <Pair />;
      case 'revoke': return <Revoke />;
      case 'history': return <History />;
      case 'hosts': return <Hosts />;
      case 'host': return <HostPage id={route.arg} />;
      case 'app': return <Construct />;
      case 'create': return <CreateVm />;
      case 'upgrade': return <Construct />;
    }
  })();
  return <>{asks}{body}</>;
}
